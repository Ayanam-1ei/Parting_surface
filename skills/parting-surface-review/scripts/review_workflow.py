# -*- coding: utf-8 -*-
from __future__ import annotations

import argparse
import json
import shutil
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Dict, Optional

from geometry_pipeline import GeometryPipelineError, analyze_prt, sha256_file
from repair_copy import RepairCopyError, prepare_working_copy
from review_engine import ReviewEngine, ReviewEngineError


SESSION_SCHEMA_VERSION = "1.0"


class WorkflowError(RuntimeError):
    pass


def run_review_workflow(
    source_path: Path,
    session_directory: Path,
    nx_root: Optional[str] = None,
    csc: Optional[str] = None,
    max_rounds: int = 5,
    keep_work: bool = False,
    prepare_copy: bool = False,
) -> Dict:
    source_path = source_path.resolve()
    session_directory = session_directory.resolve()
    if source_path.suffix.lower() != ".prt" or not source_path.is_file():
        raise WorkflowError("输入必须是可读 Siemens NX .prt 文件: " + str(source_path))
    if max_rounds < 1:
        raise WorkflowError("max_rounds 必须大于 0")

    session_directory.mkdir(parents=True, exist_ok=True)
    session_path = session_directory / "session.json"
    session = _load_session(session_path)
    _verify_baseline_source(session)
    if session:
        max_rounds = int(session.get("max_rounds", max_rounds))
    round_number = len(session.get("rounds", [])) + 1
    if round_number > max_rounds:
        raise WorkflowError("Session 已达到最大轮次 {}".format(max_rounds))

    source_hash_before = sha256_file(source_path)
    if session and Path(session["baseline_source"]["path"]).resolve() == source_path:
        if source_hash_before != session["baseline_source"]["sha256"]:
            raise WorkflowError("检测到原始源文件被原地修改；请恢复源文件并改用工作副本")

    round_directory = session_directory / "round-{:02d}".format(round_number)
    staging_directory = session_directory / ".round-{:02d}.tmp".format(round_number)
    if round_directory.exists() or staging_directory.exists():
        raise WorkflowError("轮次目录已存在，拒绝覆盖: " + str(round_directory))
    staging_directory.mkdir(parents=True)

    try:
        evidence = analyze_prt(
            source_path=source_path,
            output_directory=staging_directory,
            nx_root=nx_root,
            csc=csc,
            keep_work=keep_work,
        )
        source_hash_after = sha256_file(source_path)
        if source_hash_after != source_hash_before:
            raise WorkflowError("几何分析期间源文件哈希发生变化，审查中止")
        if evidence["meta"]["source_sha256"] != source_hash_before:
            raise WorkflowError("几何证据哈希与输入文件不一致")

        previous_review = _load_previous_review(session)
        engine = ReviewEngine()
        review = engine.review(
            evidence,
            previous_review=previous_review,
            round_number=round_number,
            max_rounds=max_rounds,
        )
        staging_review_path = staging_directory / "review_result.json"
        staging_report_path = staging_directory / "review_report.md"
        staging_review_path.write_text(
            json.dumps(review, ensure_ascii=False, indent=2), encoding="utf-8"
        )
        staging_report_path.write_text(engine.render_markdown(review), encoding="utf-8")

        working_copy = None
        if prepare_copy and review["repair_plan"]["may_prepare_working_copy"]:
            working_copy = prepare_working_copy(
                source_path, session_directory / "working-copies", round_number
            )
        staging_directory.replace(round_directory)
    except Exception:
        if staging_directory.exists():
            shutil.rmtree(staging_directory, ignore_errors=True)
        raise

    review_path = round_directory / "review_result.json"
    report_path = round_directory / "review_report.md"

    if not session:
        session = {
            "schema_version": SESSION_SCHEMA_VERSION,
            "session_id": "PSR-{}-{}".format(
                datetime.now().strftime("%Y%m%d%H%M%S"), source_hash_before[:10]
            ),
            "created_utc": datetime.now(timezone.utc).isoformat(),
            "max_rounds": max_rounds,
            "baseline_source": {
                "path": str(source_path),
                "sha256": source_hash_before,
            },
            "rounds": [],
        }
    session["updated_utc"] = datetime.now(timezone.utc).isoformat()
    session["rounds"].append(
        {
            "round_number": round_number,
            "input": {
                "path": str(source_path),
                "sha256": source_hash_before,
            },
            "evidence_path": str((round_directory / "geometry_evidence.json").resolve()),
            "review_path": str(review_path.resolve()),
            "report_path": str(report_path.resolve()),
            "status": review["status"],
            "working_copy_manifest": (
                working_copy.get("manifest_path") if working_copy else None
            ),
        }
    )
    _write_json_atomic(session_path, session)

    return {
        "session_path": str(session_path),
        "round_directory": str(round_directory),
        "evidence_path": str(round_directory / "geometry_evidence.json"),
        "review_path": str(review_path),
        "report_path": str(report_path),
        "working_copy_manifest": working_copy.get("manifest_path") if working_copy else None,
        "status": review["status"],
        "conclusion": review["conclusion"],
        "source_sha256_before": source_hash_before,
        "source_sha256_after": source_hash_after,
        "source_unchanged": source_hash_before == source_hash_after,
    }


def _load_session(path: Path) -> Dict:
    if not path.exists():
        return {}
    try:
        session = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as error:
        raise WorkflowError("无法读取 Session: " + str(error))
    if session.get("schema_version") != SESSION_SCHEMA_VERSION:
        raise WorkflowError("不支持的 Session schema_version")
    return session


def _verify_baseline_source(session: Dict) -> None:
    if not session:
        return
    baseline = session.get("baseline_source", {})
    baseline_path = Path(baseline.get("path", ""))
    if baseline_path.is_file() and sha256_file(baseline_path) != baseline.get("sha256"):
        raise WorkflowError("Session 原始源文件哈希已变化，拒绝继续")


def _load_previous_review(session: Dict) -> Optional[Dict]:
    rounds = session.get("rounds", [])
    if not rounds:
        return None
    path = Path(rounds[-1]["review_path"])
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as error:
        raise WorkflowError("无法读取上一轮审查结果: " + str(error))


def _write_json_atomic(path: Path, payload: Dict) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    temporary.replace(path)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="无需启动 NX GUI 的分型面尖钢确定性几何审查工作流"
    )
    parser.add_argument("source", type=Path)
    parser.add_argument("--session", type=Path)
    parser.add_argument("--nx-root")
    parser.add_argument("--csc")
    parser.add_argument("--max-rounds", type=int, default=5)
    parser.add_argument("--keep-work", action="store_true")
    parser.add_argument("--prepare-working-copy", action="store_true")
    args = parser.parse_args()

    session_directory = args.session
    if session_directory is None:
        session_directory = Path.cwd() / "review-output" / args.source.stem
    try:
        result = run_review_workflow(
            source_path=args.source,
            session_directory=session_directory,
            nx_root=args.nx_root,
            csc=args.csc,
            max_rounds=args.max_rounds,
            keep_work=args.keep_work,
            prepare_copy=args.prepare_working_copy,
        )
    except (
        GeometryPipelineError,
        RepairCopyError,
        ReviewEngineError,
        WorkflowError,
        OSError,
    ) as error:
        print("ERROR: " + str(error), file=sys.stderr)
        return 2

    for key, value in result.items():
        if value is not None:
            print("{}={}".format(key, value))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
