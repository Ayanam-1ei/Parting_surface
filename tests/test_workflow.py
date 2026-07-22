# -*- coding: utf-8 -*-
from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock


SCRIPTS = (
    Path(__file__).resolve().parents[1]
    / "skills"
    / "parting-surface-review"
    / "scripts"
)
sys.path.insert(0, str(SCRIPTS))

import geometry_pipeline
import review_workflow
from repair_copy import RepairCopyError, finalize_reviewed_copy, prepare_working_copy
from review_engine import ReviewEngine


class GeometryCandidateTests(unittest.TestCase):
    def test_candidate_requires_three_geometry_signals(self) -> None:
        narrow_face = {
            "face_tag": 10,
            "surface_class": "swept",
            "distance_to_product_mm": 0.0,
            "dimensions_mm": {"x": 0.05, "y": 50.0, "z": 0.2},
        }
        wide_face = {
            "face_tag": 11,
            "surface_class": "bsurf",
            "distance_to_product_mm": 0.0,
            "dimensions_mm": {"x": 5.0, "y": 10.0, "z": 20.0},
        }
        base_edge = {
            "edge_length_mm": 0.0003897,
            "normal_angle_deg": 22.749,
            "midpoint_mm": {"x": 975.91, "y": 320.265, "z": -294.436},
            "adjacent_count": 2,
            "face_a": 10,
            "face_b": 11,
            "curve_min_radius_mm": 258314.0,
            "edge_tag": 727361,
        }
        coplanar = dict(base_edge, edge_tag=1, normal_angle_deg=0.0)
        long_radius_only = dict(
            base_edge, edge_tag=2, edge_length_mm=50.0, curve_min_radius_mm=0.1
        )
        wide_only = dict(base_edge, edge_tag=3, face_a=11, face_b=11)

        risks, summary = geometry_pipeline._detect_sharp_steel_candidates(
            [base_edge, coplanar, long_radius_only, wide_only],
            {10: narrow_face, 11: wide_face},
        )

        self.assertEqual(summary["raw_candidate_count"], 1)
        self.assertEqual(summary["confirmed_geometry_risk_count"], 1)
        self.assertEqual(risks[0]["evidence"]["edge_tags"], [727361])
        self.assertIsNone(risks[0]["edge_radius_mm"])
        self.assertEqual(risks[0]["curve_min_radius_mm"], 258314.0)

    def test_invalid_radius_sentinel_becomes_unavailable(self) -> None:
        row = {
            "partition_id": "1",
            "body_tag": "2",
            "edge_tag": "3",
            "curve_status": "no_errors",
            "curve_class": "line",
            "interval_status": "no_errors",
            "length_status": "no_errors",
            "length": "0.001",
            "radius_status": "no_errors",
            "min_radius": "-3.14158e16",
            "adjacent_count": "2",
            "face_a": "4",
            "face_b": "5",
            "normal_status": "no_errors",
            "normal_angle_deg": "2",
            "midpoint_status": "no_errors",
            "mid_x": "1",
            "mid_y": "2",
            "mid_z": "3",
            "is_boundary": "false",
        }
        normalized = geometry_pipeline._normalize_edge(row)
        self.assertIsNone(normalized["curve_min_radius_mm"])

    def test_ug_inspect_metadata_is_multiline(self) -> None:
        text = "\nRelease: NX 2306\nPart units: Metric\nParasolid Version: 35.01.171\n"
        metadata, _ = geometry_pipeline._parse_ug_inspect(text)
        self.assertEqual(metadata["part_units"], "Metric")
        self.assertEqual(metadata["parasolid_version"], "35.01.171")


class ReviewEngineTests(unittest.TestCase):
    def setUp(self) -> None:
        self.engine = ReviewEngine()
        self.evidence = _evidence_with_risk("a" * 64)

    def test_missing_measurements_are_unavailable_not_zero(self) -> None:
        result = self.engine.review(self.evidence)
        unavailable_ids = {item["rule_id"] for item in result["unavailable_checks"]}
        self.assertEqual(result["status"], "not_passed")
        self.assertIn("SS-001", unavailable_ids)
        self.assertIn("SS-002", unavailable_ids)
        self.assertIn("SS-003", unavailable_ids)
        self.assertIn("UC-001", unavailable_ids)
        self.assertIsNone(result["issues"][0]["measurements"]["thickness_mm"])

    def test_loop_matches_same_issue_and_closes_removed_issue(self) -> None:
        first = self.engine.review(self.evidence)
        second = self.engine.review(self.evidence, first, round_number=2)
        self.assertTrue(second["change_tracking"]["same_source_hash"])
        self.assertEqual(
            second["change_tracking"]["remaining_issue_ids"],
            [first["issues"][0]["issue_id"]],
        )

        changed = _evidence_with_risk("b" * 64)
        changed["sharp_steels"] = []
        changed["sharp_steel_summary"].update(
            {
                "raw_candidate_count": 0,
                "clustered_candidate_count": 0,
                "reported_candidate_count": 0,
                "confirmed_geometry_risk_count": 0,
                "candidate_count": 0,
            }
        )
        third = self.engine.review(changed, first, round_number=2)
        self.assertIn(
            first["issues"][0]["issue_id"], third["change_tracking"]["closed_issue_ids"]
        )
        self.assertEqual(third["status"], "conditional")


class RepairCopyTests(unittest.TestCase):
    def test_copy_gate_keeps_source_and_rejects_unmodified_delivery(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source.prt"
            source.write_bytes(b"original-prt")
            manifest = prepare_working_copy(source, root / "copies", 1)
            manifest_path = Path(manifest["manifest_path"])
            working = Path(manifest["working_copy"]["path"])
            review_path = root / "review.json"
            review_path.write_text(
                json.dumps(
                    {
                        "schema_version": "2.0",
                        "review_id": "REV-1",
                        "status": "conditional",
                        "source": {
                            "full_path": str(working),
                            "source_sha256": manifest["source"]["sha256"],
                            "analysis_mode": "local_headless_parasolid",
                        },
                        "counts": {"issue_error": 0},
                    }
                ),
                encoding="utf-8",
            )
            with self.assertRaises(RepairCopyError):
                finalize_reviewed_copy(manifest_path, review_path)
            self.assertEqual(source.read_bytes(), b"original-prt")

            working.write_bytes(b"modified-prt")
            modified_hash = geometry_pipeline.sha256_file(working)
            review_path.write_text(
                json.dumps(
                    {
                        "schema_version": "2.0",
                        "review_id": "REV-2",
                        "status": "conditional",
                        "source": {
                            "full_path": str(working),
                            "source_sha256": modified_hash,
                            "analysis_mode": "local_headless_parasolid",
                        },
                        "counts": {"issue_error": 0},
                    }
                ),
                encoding="utf-8",
            )
            delivered = finalize_reviewed_copy(manifest_path, review_path)
            self.assertTrue(Path(delivered["delivery"]["path"]).is_file())
            self.assertEqual(source.read_bytes(), b"original-prt")


class WorkflowFailureTests(unittest.TestCase):
    def test_failed_round_removes_staging_directory(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source.prt"
            source.write_bytes(b"not-a-real-prt")
            session = root / "session"
            with mock.patch.object(
                review_workflow, "analyze_prt", side_effect=RuntimeError("failed")
            ):
                with self.assertRaises(RuntimeError):
                    review_workflow.run_review_workflow(source, session)
            self.assertFalse((session / ".round-01.tmp").exists())
            self.assertFalse((session / "round-01").exists())


def _evidence_with_risk(source_hash: str):
    risk = {
        "fingerprint": "abc123def0",
        "classification": "confirmed_geometry_risk",
        "coordinate_approx": {"x": 975.91, "y": 320.265, "z": -294.436},
        "representative_edge_length_mm": 0.0003897,
        "wedge_angle_deg": 22.749,
        "min_narrow_face_dimension_mm": 0.0028,
        "distance_to_product_mm": 0.00002,
        "thickness_mm": None,
        "height_mm": None,
        "aspect_ratio": None,
        "edge_radius_mm": None,
        "curve_min_radius_mm": 258314.0,
        "evidence": {"edge_tags": [727361], "face_tags": [726489, 727317]},
    }
    return {
        "schema_version": "2.0",
        "meta": {
            "file_name": "part.prt",
            "full_path": "part.prt",
            "source_sha256": source_hash,
            "analysis_mode": "local_headless_parasolid",
        },
        "bodies": [{"body_tag": 1}],
        "parting_surface": {
            "measurement_status": "measured",
            "is_planar": False,
            "flatness_score": 2.2,
            "face_count": 425,
        },
        "parting_line": {"is_at_max_contour": None},
        "sharp_steels": [risk],
        "sharp_steel_summary": {
            "raw_candidate_count": 1,
            "clustered_candidate_count": 1,
            "reported_candidate_count": 1,
            "omitted_candidate_count": 0,
            "confirmed_geometry_risk_count": 1,
            "candidate_count": 0,
        },
        "undercuts": [],
        "undercut_analysis": {"status": "unavailable", "reason": "缺少开模方向"},
        "limitations": ["钢厚不可用"],
    }


if __name__ == "__main__":
    unittest.main()
