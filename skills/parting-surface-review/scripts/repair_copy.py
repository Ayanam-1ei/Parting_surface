# -*- coding: utf-8 -*-
from __future__ import annotations

import argparse
import json
import shutil
from datetime import datetime, timezone
from pathlib import Path
from typing import Dict, Optional

from geometry_pipeline import sha256_file


class RepairCopyError(RuntimeError):
    pass


def prepare_working_copy(
    source_path: Path,
    output_directory: Path,
    round_number: int,
) -> Dict:
    source_path = source_path.resolve()
    output_directory = output_directory.resolve()
    _validate_prt(source_path)
    output_directory.mkdir(parents=True, exist_ok=True)

    source_hash_before = sha256_file(source_path)
    working_path = output_directory / (
        "{}_working_r{:02d}.prt".format(source_path.stem, round_number)
    )
    manifest_path = working_path.with_suffix(".manifest.json")
    if working_path.exists() or manifest_path.exists():
        raise RepairCopyError("工作副本或清单已存在，拒绝覆盖: " + str(working_path))

    shutil.copy2(str(source_path), str(working_path))
    source_hash_after = sha256_file(source_path)
    working_hash = sha256_file(working_path)
    if source_hash_after != source_hash_before:
        working_path.unlink(missing_ok=True)
        raise RepairCopyError("复制期间源文件哈希发生变化，已取消")
    if working_hash != source_hash_before:
        working_path.unlink(missing_ok=True)
        raise RepairCopyError("工作副本与源文件哈希不一致")

    manifest = {
        "schema_version": "1.0",
        "created_utc": datetime.now(timezone.utc).isoformat(),
        "status": "unmodified_working_copy",
        "source": {
            "path": str(source_path),
            "sha256": source_hash_before,
        },
        "working_copy": {
            "path": str(working_path),
            "sha256": working_hash,
        },
        "round_number": round_number,
        "source_modified_by_workflow": False,
        "delivery_allowed": False,
        "delivery_reason": "仅为未修改副本；尚无合法 NX Headless 修改成功证据和复审结果。",
    }
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    manifest["manifest_path"] = str(manifest_path)
    return manifest


def finalize_reviewed_copy(
    manifest_path: Path,
    review_result_path: Path,
    output_path: Optional[Path] = None,
) -> Dict:
    manifest_path = manifest_path.resolve()
    review_result_path = review_result_path.resolve()
    manifest = _load_json(manifest_path)
    review = _load_json(review_result_path)
    if manifest.get("status") != "unmodified_working_copy":
        raise RepairCopyError("清单状态不允许交付: " + str(manifest.get("status")))

    source_path = Path(manifest["source"]["path"]).resolve()
    working_path = Path(manifest["working_copy"]["path"]).resolve()
    _validate_prt(source_path)
    _validate_prt(working_path)
    source_hash = sha256_file(source_path)
    working_hash = sha256_file(working_path)
    if source_hash != manifest["source"]["sha256"]:
        raise RepairCopyError("原始源文件已变化，拒绝交付")
    if working_hash == source_hash:
        raise RepairCopyError("工作副本尚未修改，不能命名为审后版本")
    review_source = review.get("source", {})
    if review.get("schema_version") != "2.0":
        raise RepairCopyError("复审结果 schema_version 无效")
    if review_source.get("analysis_mode") != "local_headless_parasolid":
        raise RepairCopyError("复审结果不是本地确定性 Parasolid 审查")
    reviewed_path = Path(review_source.get("full_path", "")).resolve()
    if reviewed_path != working_path:
        raise RepairCopyError("复审结果文件路径与工作副本不一致")
    if review_source.get("source_sha256") != working_hash:
        raise RepairCopyError("复审结果不是针对当前工作副本")
    if review.get("status") in ("not_passed", "stopped_incomplete"):
        raise RepairCopyError("复审仍存在确认风险，拒绝交付")
    if review.get("counts", {}).get("issue_error", 0):
        raise RepairCopyError("复审仍有 ERROR，拒绝交付")

    if output_path is None:
        output_path = working_path.with_name(
            working_path.stem.replace("_working_", "_reviewed_") + ".prt"
        )
    output_path = output_path.resolve()
    if output_path == source_path or output_path == working_path:
        raise RepairCopyError("交付路径不得覆盖源文件或工作副本")
    if output_path.exists():
        raise RepairCopyError("交付文件已存在，拒绝覆盖: " + str(output_path))

    output_path.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(str(working_path), str(output_path))
    if sha256_file(source_path) != source_hash:
        output_path.unlink(missing_ok=True)
        raise RepairCopyError("交付期间源文件发生变化，已取消")
    if sha256_file(output_path) != working_hash:
        output_path.unlink(missing_ok=True)
        raise RepairCopyError("交付副本哈希校验失败")

    final_manifest = dict(manifest)
    final_manifest.update(
        {
            "finalized_utc": datetime.now(timezone.utc).isoformat(),
            "status": "reviewed_modified_copy",
            "working_copy": {
                "path": str(working_path),
                "sha256": working_hash,
            },
            "review": {
                "path": str(review_result_path),
                "review_id": review.get("review_id"),
                "status": review.get("status"),
            },
            "delivery": {
                "path": str(output_path),
                "sha256": working_hash,
            },
            "delivery_allowed": True,
            "delivery_reason": "工作副本已变化、源文件未变化，且修改副本复审无 ERROR。",
        }
    )
    final_path = output_path.with_suffix(".manifest.json")
    final_path.write_text(
        json.dumps(final_manifest, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    final_manifest["manifest_path"] = str(final_path)
    return final_manifest


def _validate_prt(path: Path) -> None:
    if path.suffix.lower() != ".prt" or not path.is_file():
        raise RepairCopyError("不是可读 .prt 文件: " + str(path))


def _load_json(path: Path) -> Dict:
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError, KeyError) as error:
        raise RepairCopyError("无法读取清单或复审结果: " + str(error))


def main() -> int:
    parser = argparse.ArgumentParser(description="保护 NX 源文件并管理修改工作副本")
    subparsers = parser.add_subparsers(dest="command", required=True)
    prepare_parser = subparsers.add_parser("prepare")
    prepare_parser.add_argument("source", type=Path)
    prepare_parser.add_argument("output_directory", type=Path)
    prepare_parser.add_argument("--round", type=int, default=1)
    finalize_parser = subparsers.add_parser("finalize")
    finalize_parser.add_argument("manifest", type=Path)
    finalize_parser.add_argument("review_result", type=Path)
    finalize_parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    if args.command == "prepare":
        result = prepare_working_copy(args.source, args.output_directory, args.round)
    else:
        result = finalize_reviewed_copy(args.manifest, args.review_result, args.output)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
