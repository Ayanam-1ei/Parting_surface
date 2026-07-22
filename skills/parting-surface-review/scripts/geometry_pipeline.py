# -*- coding: utf-8 -*-
from __future__ import annotations

import csv
import hashlib
import json
import math
import os
import re
import shutil
import subprocess
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

from runtime_locator import RuntimePaths, locate_runtime


SCHEMA_VERSION = "2.0"
NX_PARASOLID_TO_MM = 1000.0
DEFAULT_SHARP_STEEL_POLICY = {
    "near_product_max_mm": 0.1,
    "valid_curve_radius_max_mm": 1000000.0,
    "narrow_nonplanar_face_max_mm": 1.0,
    "confirmed_max_edge_length_mm": 0.1,
    "confirmed_min_angle_deg": 1.0,
    "confirmed_max_angle_deg": 45.0,
    "candidate_max_edge_length_mm": 1.5,
    "candidate_min_angle_deg": 0.5,
    "candidate_max_angle_deg": 15.0,
    "cluster_radius_mm": 2.0,
    "max_reported_candidates": 30,
}


class GeometryPipelineError(RuntimeError):
    pass


@dataclass(frozen=True)
class Partition:
    partition_id: int
    label: str
    extracted_path: Path


def analyze_prt(
    source_path: Path,
    output_directory: Path,
    nx_root: Optional[str] = None,
    csc: Optional[str] = None,
    keep_work: bool = False,
) -> Dict:
    source_path = source_path.resolve()
    output_directory = output_directory.resolve()
    if source_path.suffix.lower() != ".prt":
        raise GeometryPipelineError("只接受 Siemens NX .prt 文件: " + str(source_path))
    if not source_path.is_file():
        raise GeometryPipelineError("文件不存在: " + str(source_path))

    runtime = locate_runtime(nx_root=nx_root, csc=csc)
    output_directory.mkdir(parents=True, exist_ok=True)
    work_directory = output_directory / ".work"
    if work_directory.exists():
        _remove_tree(work_directory)
    partitions_directory = work_directory / "partitions"
    raw_directory = work_directory / "raw"
    bin_directory = work_directory / "bin"
    partitions_directory.mkdir(parents=True)
    raw_directory.mkdir(parents=True)
    bin_directory.mkdir(parents=True)

    inspect_text = _run_text([str(runtime.ug_inspect), "-ps", str(source_path)])
    part_metadata, partition_descriptors = _parse_ug_inspect(inspect_text)
    if not partition_descriptors:
        raise GeometryPipelineError(".prt 中没有可读取的 Parasolid 分区")

    partitions = _extract_partitions(
        runtime=runtime,
        source_path=source_path,
        descriptors=partition_descriptors,
        output_directory=partitions_directory,
    )
    analyzer_executable = _build_analyzer(runtime, bin_directory)
    _run_analyzer(runtime, analyzer_executable, raw_directory, partitions)
    evidence = _assemble_evidence(
        source_path=source_path,
        source_sha256=sha256_file(source_path),
        runtime=runtime,
        part_metadata=part_metadata,
        raw_directory=raw_directory,
    )
    evidence_path = output_directory / "geometry_evidence.json"
    evidence_path.write_text(json.dumps(evidence, ensure_ascii=False, indent=2), encoding="utf-8")
    if not keep_work:
        _remove_tree(work_directory)
    return evidence


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while True:
            block = stream.read(1024 * 1024)
            if not block:
                break
            digest.update(block)
    return digest.hexdigest()


def _run_text(
    command: Sequence[str],
    env: Optional[Dict[str, str]] = None,
    allowed_returncodes: Sequence[int] = (0,),
) -> str:
    completed = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=env)
    text = _decode_console(completed.stdout)
    if completed.returncode not in allowed_returncodes:
        raise GeometryPipelineError(
            "命令执行失败 ({0}):\n{1}".format(completed.returncode, text.strip())
        )
    return text


def _decode_console(data: bytes) -> str:
    for encoding in ("utf-8", "gb18030", "mbcs"):
        try:
            return data.decode(encoding)
        except (UnicodeDecodeError, LookupError):
            continue
    return data.decode("utf-8", errors="replace")


def _parse_ug_inspect(text: str) -> Tuple[Dict, List[Tuple[int, str]]]:
    partition_ids = [int(value) for value in re.findall(r"Partition id:\s*(\d+)", text)]
    labels: Dict[int, str] = {}
    directory_pattern = re.compile(
        r"^\s*(Parasolid|PS\s+Tool|PS\s+Sheet)\s+\d+\s+(\d+)\s+\(", re.MULTILINE | re.IGNORECASE
    )
    for match in directory_pattern.finditer(text):
        labels[int(match.group(2))] = re.sub(r"\s+", " ", match.group(1)).strip()
    descriptors = [(partition_id, labels.get(partition_id, "Parasolid")) for partition_id in partition_ids]
    descriptors = list(dict.fromkeys(descriptors))

    def match_value(pattern: str) -> Optional[str]:
        match = re.search(pattern, text, re.IGNORECASE | re.MULTILINE)
        return match.group(1).strip() if match else None

    metadata = {
        "nx_release": match_value(r"^Release:\s*(.+)$") or match_value(r"last saved in:\s*(NX[^\r\n]+)"),
        "part_units": match_value(r"^Part units:\s*(.+)$"),
        "parasolid_version": match_value(r"^Parasolid Version:\s*(.+)$"),
        "native_mode": "last saved in native mode" in text.lower(),
        "partition_count": len(descriptors),
    }
    return metadata, descriptors


def _extract_partitions(
    runtime: RuntimePaths,
    source_path: Path,
    descriptors: Iterable[Tuple[int, str]],
    output_directory: Path,
) -> List[Partition]:
    extracted = []
    for partition_id, label in descriptors:
        base_path = output_directory / ("partition_{0}".format(partition_id))
        before = set(output_directory.iterdir())
        _run_text(
            [str(runtime.ug_inspect), "-extract", str(partition_id), str(source_path), str(base_path)],
            allowed_returncodes=(0, 1),
        )
        created = [path for path in output_directory.iterdir() if path not in before and path.is_file()]
        candidates = [path for path in created if path.stem.startswith(base_path.name)]
        if not candidates:
            candidates = list(output_directory.glob(base_path.name + "*"))
        if not candidates:
            raise GeometryPipelineError("Parasolid 分区提取失败: " + str(partition_id))
        extracted_path = max(candidates, key=lambda path: path.stat().st_size)
        extracted.append(Partition(partition_id, label, extracted_path.resolve()))
    return extracted


def _build_analyzer(runtime: RuntimePaths, bin_directory: Path) -> Path:
    project_root = Path(__file__).resolve().parents[3]
    source_path = project_root / "analyzer" / "ParasolidAnalyzer.cs"
    if not source_path.exists():
        raise GeometryPipelineError("缺少几何分析器源码: " + str(source_path))
    executable = bin_directory / "PartingSurface.ParasolidAnalyzer.exe"
    managed_copy = bin_directory / "pskernel_net.dll"
    shutil.copyfile(runtime.pskernel_net, managed_copy)
    command = [
        str(runtime.csc),
        "/nologo",
        "/unsafe",
        "/platform:x64",
        "/reference:" + str(runtime.pskernel_net),
        "/out:" + str(executable),
        str(source_path),
    ]
    _run_text(command)
    if not executable.exists():
        raise GeometryPipelineError("C# 几何分析器编译后未生成可执行文件")
    return executable


def _run_analyzer(
    runtime: RuntimePaths,
    executable: Path,
    output_directory: Path,
    partitions: Sequence[Partition],
) -> None:
    specifications = [
        "{0}|{1}|{2}".format(partition.partition_id, partition.label, partition.extracted_path)
        for partition in partitions
    ]
    environment = os.environ.copy()
    environment["PATH"] = str(runtime.nxbin) + os.pathsep + environment.get("PATH", "")
    _run_text([str(executable), str(output_directory)] + specifications, env=environment)
    for required in ("bodies.tsv", "faces.tsv", "edges.tsv"):
        if not (output_directory / required).exists():
            raise GeometryPipelineError("几何分析器缺少输出: " + required)


def _assemble_evidence(
    source_path: Path,
    source_sha256: str,
    runtime: RuntimePaths,
    part_metadata: Dict,
    raw_directory: Path,
) -> Dict:
    bodies = [_normalize_body(row) for row in _read_tsv(raw_directory / "bodies.tsv")]
    faces = [_normalize_face(row) for row in _read_tsv(raw_directory / "faces.tsv")]
    edges = [_normalize_edge(row) for row in _read_tsv(raw_directory / "edges.tsv")]
    _mark_duplicate_bodies(bodies)
    reference_body = next((body for body in bodies if body["is_reference_solid"]), None)
    sheet_bodies = [body for body in bodies if body["body_type"] == "sheet_c"]
    face_by_tag = {face["face_tag"]: face for face in faces}
    sharp_steel_policy = _load_candidate_policy()
    sharp_steels, sharp_steel_summary = _detect_sharp_steel_candidates(
        edges, face_by_tag, sharp_steel_policy
    )
    surface_counts: Dict[str, int] = {}
    for face in faces:
        surface_counts[face["surface_class"]] = surface_counts.get(face["surface_class"], 0) + 1

    product = _build_product(reference_body)
    parting_surface = _build_parting_surface(sheet_bodies, faces, edges, surface_counts)
    parting_line = _build_parting_line(parting_surface, sharp_steels)
    limitations = []
    solid_bodies = [body for body in bodies if body["body_type"] == "solid_c" and not body["is_duplicate"]]
    exact_steel_available = len(solid_bodies) >= 3
    if not exact_steel_available:
        limitations.append(
            "未识别到独立型腔/型芯钢料实体；尖钢厚度、有效高度和细长比保持 unavailable。"
        )
    limitations.append("未提供开模方向；倒扣和最大轮廓分型位置不作通过判断。")

    return {
        "schema_version": SCHEMA_VERSION,
        "meta": {
            "file_name": source_path.name,
            "full_path": str(source_path),
            "source_sha256": source_sha256,
            "extract_time_utc": datetime.now(timezone.utc).isoformat(),
            "nx_release": part_metadata.get("nx_release"),
            "part_units": part_metadata.get("part_units"),
            "parasolid_version": part_metadata.get("parasolid_version"),
            "analysis_mode": "local_headless_parasolid",
            "coordinate_scale_to_mm": NX_PARASOLID_TO_MM,
            "nx_root": str(runtime.nx_root),
        },
        "measurement_policy": {
            "missing_numeric_values": None,
            "edge_length_is_not_steel_thickness": True,
            "classification_levels": ["confirmed_geometry_risk", "candidate", "unavailable"],
            "sharp_steel_candidate_policy": sharp_steel_policy,
        },
        "bodies": bodies,
        "role_assignment": {
            "reference_product_body_tag": reference_body["body_tag"] if reference_body else None,
            "parting_surface_body_tags": [body["body_tag"] for body in sheet_bodies],
            "exact_steel_measurement_available": exact_steel_available,
        },
        "product": product,
        "parting_surface": parting_surface,
        "parting_line": parting_line,
        "sharp_steels": sharp_steels,
        "sharp_steel_summary": sharp_steel_summary,
        "undercuts": [],
        "undercut_analysis": {
            "status": "unavailable",
            "reason": "需要开模方向和产品外表面可见性分析。",
            "pull_direction": None,
        },
        "mold": {
            "cavity_material": None,
            "core_material": None,
            "expected_shot_life_k": None,
        },
        "limitations": limitations,
    }


def _read_tsv(path: Path) -> List[Dict[str, str]]:
    with path.open("r", encoding="utf-8", newline="") as stream:
        return list(csv.DictReader(stream, delimiter="\t"))


def _normalize_body(row: Dict[str, str]) -> Dict:
    box = _box_mm(row)
    return {
        "partition_id": _int(row["partition_id"]),
        "partition_label": row["partition_label"],
        "body_tag": _int(row["body_tag"]),
        "body_type": row["body_type"],
        "face_count": _int(row["face_count"]),
        "edge_count": _int(row["edge_count"]),
        "vertex_count": _int(row["vertex_count"]),
        "bbox_mm": box,
        "dimensions_mm": _dimensions(box),
        "is_reference_solid": row["is_reference_solid"] == "true",
        "is_duplicate": False,
        "duplicate_of_body_tag": None,
    }


def _normalize_face(row: Dict[str, str]) -> Dict:
    box = _box_mm(row)
    return {
        "partition_id": _int(row["partition_id"]),
        "body_tag": _int(row["body_tag"]),
        "face_tag": _int(row["face_tag"]),
        "surface_class": row["surface_class"],
        "bbox_mm": box,
        "dimensions_mm": _dimensions(box),
        "range_status": row["range_status"],
        "distance_to_product_mm": _scaled_float(row["distance"]),
        "closest_to_product_mm": _vector_mm(row, "closest"),
        "normal_status": row["normal_status"],
        "normal": _vector(row, "normal"),
        "interior_point_mm": _vector_mm(row, "interior"),
    }


def _normalize_edge(row: Dict[str, str]) -> Dict:
    radius = _scaled_float(row["min_radius"])
    if (
        row["radius_status"] != "no_errors"
        or radius is None
        or radius <= 0.0
        or radius > DEFAULT_SHARP_STEEL_POLICY["valid_curve_radius_max_mm"]
    ):
        radius = None
    return {
        "partition_id": _int(row["partition_id"]),
        "body_tag": _int(row["body_tag"]),
        "edge_tag": _int(row["edge_tag"]),
        "curve_status": row["curve_status"],
        "curve_class": row["curve_class"],
        "interval_status": row["interval_status"],
        "length_status": row["length_status"],
        "edge_length_mm": _scaled_float(row["length"]),
        "radius_status": row["radius_status"],
        "curve_min_radius_mm": radius,
        "adjacent_count": _int(row["adjacent_count"]),
        "face_a": _int(row["face_a"]),
        "face_b": _int(row["face_b"]),
        "normal_status": row["normal_status"],
        "normal_angle_deg": _float(row["normal_angle_deg"]),
        "midpoint_status": row["midpoint_status"],
        "midpoint_mm": _vector_mm(row, "mid"),
        "is_boundary": row["is_boundary"] == "true",
    }


def _mark_duplicate_bodies(bodies: List[Dict]) -> None:
    first_by_signature: Dict[Tuple, Dict] = {}
    for body in bodies:
        box = body["bbox_mm"]
        signature = (
            body["body_type"],
            body["face_count"],
            body["edge_count"],
            body["vertex_count"],
            tuple(round(value, 3) for value in box["min"] + box["max"]),
        )
        original = first_by_signature.get(signature)
        if original:
            body["is_duplicate"] = True
            body["duplicate_of_body_tag"] = original["body_tag"]
        else:
            first_by_signature[signature] = body


def _build_product(reference_body: Optional[Dict]) -> Dict:
    if not reference_body:
        return {
            "measurement_status": "unavailable",
            "material": None,
            "bbox_mm": None,
            "dimensions_mm": None,
            "max_span_mm": None,
            "nominal_wall_thickness_mm": None,
            "max_projected_area_cm2": None,
        }
    dimensions = reference_body["dimensions_mm"]
    return {
        "measurement_status": "measured_bbox_only",
        "body_tag": reference_body["body_tag"],
        "material": None,
        "bbox_mm": reference_body["bbox_mm"],
        "dimensions_mm": dimensions,
        "max_span_mm": max(dimensions.values()),
        "nominal_wall_thickness_mm": None,
        "max_projected_area_cm2": None,
    }


def _build_parting_surface(
    sheet_bodies: List[Dict], faces: List[Dict], edges: List[Dict], surface_counts: Dict[str, int]
) -> Dict:
    face_count = len(faces)
    plane_count = surface_counts.get("plane", 0)
    flatness_score = round(10.0 * plane_count / face_count, 1) if face_count else None
    measured_distances = [
        face["distance_to_product_mm"]
        for face in faces
        if face["distance_to_product_mm"] is not None and face["range_status"] == "found_c"
    ]
    return {
        "measurement_status": "measured" if sheet_bodies else "unavailable",
        "body_tags": [body["body_tag"] for body in sheet_bodies],
        "face_count": face_count,
        "edge_count": len(edges),
        "surface_type_counts": surface_counts,
        "flatness_score": flatness_score,
        "is_planar": face_count > 0 and plane_count == face_count,
        "bbox_mm": sheet_bodies[0]["bbox_mm"] if len(sheet_bodies) == 1 else None,
        "min_face_distance_to_product_mm": min(measured_distances) if measured_distances else None,
        "faces_within_0_1_mm": sum(1 for value in measured_distances if value <= 0.1),
        "faces_within_5_mm": sum(1 for value in measured_distances if value <= 5.0),
    }


def _build_parting_line(parting_surface: Dict, sharp_steels: List[Dict]) -> Dict:
    if parting_surface["measurement_status"] == "unavailable":
        return {
            "measurement_status": "unavailable",
            "shape_type": None,
            "flatness_score": None,
            "coordinate_y_mm": None,
            "is_at_max_contour": None,
        }
    return {
        "measurement_status": "surface_inferred",
        "location_relative_to_product": None,
        "shape_type": "flat" if parting_surface["is_planar"] else "complex_surface",
        "flatness_score": parting_surface["flatness_score"],
        "coordinate_y_mm": None,
        "max_product_diameter_at_pl_mm": None,
        "is_at_max_contour": None,
        "risk_candidate_count": len(sharp_steels),
    }


def _detect_sharp_steel_candidates(
    edges: List[Dict], face_by_tag: Dict[int, Dict], policy: Optional[Dict] = None
) -> Tuple[List[Dict], Dict]:
    policy = dict(DEFAULT_SHARP_STEEL_POLICY, **(policy or {}))
    raw = []
    for edge in edges:
        length = edge["edge_length_mm"]
        angle = edge["normal_angle_deg"]
        midpoint = edge["midpoint_mm"]
        if length is None or angle is None or midpoint is None or edge["adjacent_count"] < 2:
            continue
        face_a = face_by_tag.get(edge["face_a"])
        face_b = face_by_tag.get(edge["face_b"])
        distances = [
            face["distance_to_product_mm"]
            for face in (face_a, face_b)
            if face and face["distance_to_product_mm"] is not None
        ]
        near_distance = min(distances) if distances else None
        near_product = (
            near_distance is not None and near_distance <= policy["near_product_max_mm"]
        )
        narrow_faces = []
        for face in (face_a, face_b):
            if not face or "plane" in face["surface_class"].lower():
                continue
            dimensions = face.get("dimensions_mm") or {}
            positive_dimensions = [value for value in dimensions.values() if value >= 0.0]
            if positive_dimensions:
                minimum_dimension = min(positive_dimensions)
                if minimum_dimension <= policy["narrow_nonplanar_face_max_mm"]:
                    narrow_faces.append((face["face_tag"], minimum_dimension))

        confirmed = (
            length <= policy["confirmed_max_edge_length_mm"]
            and policy["confirmed_min_angle_deg"] <= angle <= policy["confirmed_max_angle_deg"]
        )
        candidate = (
            length <= policy["candidate_max_edge_length_mm"]
            and policy["candidate_min_angle_deg"] <= angle <= policy["candidate_max_angle_deg"]
        )
        if not near_product or not narrow_faces or not (confirmed or candidate):
            continue

        radius = edge["curve_min_radius_mm"]
        classification = "confirmed_geometry_risk" if confirmed else "candidate"
        score = 100 if confirmed else 60
        reasons = ["near_product", "narrow_nonplanar_face"]
        reasons.append("micro_edge" if length <= 0.1 else "short_edge")
        reasons.append("measurable_face_angle")
        if radius is not None and radius < 0.5:
            score += 5
            reasons.append("small_curve_radius")
        confidence = 0.9 if confirmed else 0.65
        raw.append(
            {
                "score": score,
                "classification": classification,
                "confidence": round(confidence, 2),
                "coordinate": midpoint,
                "edge_length_mm": length,
                "normal_angle_deg": angle,
                "curve_min_radius_mm": radius,
                "distance_to_product_mm": near_distance,
                "edge_tags": [edge["edge_tag"]],
                "face_tags": [tag for tag in (edge["face_a"], edge["face_b"]) if tag],
                "narrow_face_tags": [item[0] for item in narrow_faces],
                "min_narrow_face_dimension_mm": min(item[1] for item in narrow_faces),
                "reasons": reasons,
            }
        )

    clustered = _cluster_candidates(
        sorted(raw, key=lambda item: (-item["score"], item["edge_length_mm"])),
        radius_mm=policy["cluster_radius_mm"],
    )
    results = []
    for item in clustered:
        fingerprint_text = "{0:.1f}|{1:.1f}|{2:.1f}".format(
            item["coordinate"]["x"],
            item["coordinate"]["y"],
            item["coordinate"]["z"],
        )
        fingerprint = hashlib.sha1(fingerprint_text.encode("utf-8")).hexdigest()[:10]
        results.append(
            {
                "id": "SS-" + fingerprint.upper(),
                "fingerprint": fingerprint,
                "classification": item["classification"],
                "confidence": item["confidence"],
                "severity": "ERROR" if item["classification"] == "confirmed_geometry_risk" else "WARN",
                "position": "parting_surface_edge_cluster",
                "coordinate_approx": item["coordinate"],
                "measurement_mode": "surface_only",
                "thickness_mm": None,
                "height_mm": None,
                "aspect_ratio": None,
                "edge_radius_mm": None,
                "curve_min_radius_mm": item["curve_min_radius_mm"],
                "wedge_angle_deg": item["normal_angle_deg"],
                "representative_edge_length_mm": item["edge_length_mm"],
                "distance_to_product_mm": item["distance_to_product_mm"],
                "min_narrow_face_dimension_mm": item["min_narrow_face_dimension_mm"],
                "is_on_parting_line": True,
                "is_in_high_pressure_zone": None,
                "evidence": {
                    "edge_tags": sorted(set(item["edge_tags"])),
                    "face_tags": sorted(set(item["face_tags"])),
                    "narrow_face_tags": sorted(set(item["narrow_face_tags"])),
                    "reasons": sorted(set(item["reasons"])),
                    "note": "边长和曲线最小半径仅用于拓扑筛查，不作为钢厚或钢料圆角。",
                },
            }
        )
    results.sort(
        key=lambda item: (
            0 if item["classification"] == "confirmed_geometry_risk" else 1,
            item["representative_edge_length_mm"],
        )
    )
    reported = results[: int(policy["max_reported_candidates"])]
    summary = {
        "raw_candidate_count": len(raw),
        "clustered_candidate_count": len(results),
        "reported_candidate_count": len(reported),
        "omitted_candidate_count": max(0, len(results) - len(reported)),
        "confirmed_geometry_risk_count": sum(
            1 for item in results if item["classification"] == "confirmed_geometry_risk"
        ),
        "candidate_count": sum(1 for item in results if item["classification"] == "candidate"),
        "exact_thickness_count": 0,
    }
    return reported, summary


def _load_candidate_policy() -> Dict:
    rules_path = Path(__file__).resolve().parents[1] / "rules" / "review_rules.json"
    try:
        payload = json.loads(rules_path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as error:
        raise GeometryPipelineError("无法读取尖钢候选规则: " + str(error))
    configured = payload.get("sharp_steel_candidate_policy")
    if not isinstance(configured, dict):
        raise GeometryPipelineError("review_rules.json 缺少 sharp_steel_candidate_policy")
    return dict(DEFAULT_SHARP_STEEL_POLICY, **configured)


def _cluster_candidates(candidates: List[Dict], radius_mm: float = 2.0) -> List[Dict]:
    clusters: List[Dict] = []
    for candidate in candidates:
        target = next(
            (
                cluster
                for cluster in clusters
                if _point_distance(cluster["coordinate"], candidate["coordinate"]) <= radius_mm
            ),
            None,
        )
        if target is None:
            clusters.append(dict(candidate))
            continue
        target["edge_tags"].extend(candidate["edge_tags"])
        target["face_tags"].extend(candidate["face_tags"])
        target["narrow_face_tags"].extend(candidate["narrow_face_tags"])
        target["reasons"].extend(candidate["reasons"])
        if candidate["score"] > target["score"]:
            for key in (
                "score",
                "classification",
                "confidence",
                "coordinate",
                "edge_length_mm",
                "normal_angle_deg",
                "curve_min_radius_mm",
                "distance_to_product_mm",
                "min_narrow_face_dimension_mm",
            ):
                target[key] = candidate[key]
    return clusters


def _point_distance(first: Dict[str, float], second: Dict[str, float]) -> float:
    return math.sqrt(
        (first["x"] - second["x"]) ** 2
        + (first["y"] - second["y"]) ** 2
        + (first["z"] - second["z"]) ** 2
    )


def _box_mm(row: Dict[str, str]) -> Dict[str, List[float]]:
    return {
        "min": [
            _required_float(row["min_x"]) * NX_PARASOLID_TO_MM,
            _required_float(row["min_y"]) * NX_PARASOLID_TO_MM,
            _required_float(row["min_z"]) * NX_PARASOLID_TO_MM,
        ],
        "max": [
            _required_float(row["max_x"]) * NX_PARASOLID_TO_MM,
            _required_float(row["max_y"]) * NX_PARASOLID_TO_MM,
            _required_float(row["max_z"]) * NX_PARASOLID_TO_MM,
        ],
    }


def _dimensions(box: Dict[str, List[float]]) -> Dict[str, float]:
    return {
        "x": box["max"][0] - box["min"][0],
        "y": box["max"][1] - box["min"][1],
        "z": box["max"][2] - box["min"][2],
    }


def _vector_mm(row: Dict[str, str], prefix: str) -> Optional[Dict[str, float]]:
    vector = _vector(row, prefix)
    if vector is None:
        return None
    return {axis: value * NX_PARASOLID_TO_MM for axis, value in vector.items()}


def _vector(row: Dict[str, str], prefix: str) -> Optional[Dict[str, float]]:
    values = [_float(row.get(prefix + "_" + axis, "")) for axis in ("x", "y", "z")]
    if any(value is None for value in values):
        return None
    return {"x": values[0], "y": values[1], "z": values[2]}


def _scaled_float(value: str) -> Optional[float]:
    parsed = _float(value)
    return parsed * NX_PARASOLID_TO_MM if parsed is not None else None


def _float(value: str) -> Optional[float]:
    if value is None or value == "":
        return None
    parsed = float(value)
    return parsed if math.isfinite(parsed) else None


def _required_float(value: str) -> float:
    parsed = _float(value)
    if parsed is None:
        raise GeometryPipelineError("几何输出包含缺失的必需数值")
    return parsed


def _int(value: str) -> int:
    return int(value or 0)


def _remove_tree(path: Path) -> None:
    if not path.exists():
        return
    for child in path.rglob("*"):
        try:
            child.chmod(0o700)
        except OSError:
            pass
    try:
        path.chmod(0o700)
    except OSError:
        pass
    shutil.rmtree(path, ignore_errors=False)
