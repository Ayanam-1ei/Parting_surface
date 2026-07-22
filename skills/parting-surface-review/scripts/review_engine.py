# -*- coding: utf-8 -*-
from __future__ import annotations

import argparse
import copy
import json
import math
from datetime import datetime, timezone
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Tuple


REVIEW_SCHEMA_VERSION = "2.0"
STATUS_LABELS = {
    "passed": "通过",
    "conditional": "有条件通过，需工程确认",
    "not_passed": "不通过，存在确认几何风险",
    "stopped_incomplete": "达到最大轮次，仍未闭环",
}


class ReviewEngineError(RuntimeError):
    pass


class ReviewEngine:
    def __init__(self, rules_path: Optional[Path] = None) -> None:
        if rules_path is None:
            rules_path = Path(__file__).resolve().parents[1] / "rules" / "review_rules.json"
        try:
            self.rules_document = json.loads(rules_path.read_text(encoding="utf-8-sig"))
        except (OSError, ValueError) as error:
            raise ReviewEngineError("无法读取审查规则: " + str(error))
        self.rules = {
            rule["id"]: rule for rule in self.rules_document.get("rules", []) if "id" in rule
        }
        self.policy = self.rules_document.get("sharp_steel_candidate_policy", {})

    def review(
        self,
        evidence: Dict,
        previous_review: Optional[Dict] = None,
        round_number: int = 1,
        max_rounds: int = 5,
    ) -> Dict:
        self._validate_evidence(evidence)
        if round_number < 1 or max_rounds < 1:
            raise ReviewEngineError("round_number 和 max_rounds 必须大于 0")

        rule_results = self._evaluate_rules(evidence)
        issues = self._build_geometry_issues(evidence)
        issues, change_tracking = self._track_changes(
            issues, previous_review, evidence["meta"]["source_sha256"]
        )
        counts = self._build_counts(rule_results, issues)
        status = self._determine_status(counts)
        if round_number >= max_rounds and status != "passed":
            status = "stopped_incomplete"

        source = evidence["meta"]
        review_id = "REV-{0}-{1:02d}".format(
            datetime.now().strftime("%Y%m%d%H%M%S"), round_number
        )
        result = {
            "schema_version": REVIEW_SCHEMA_VERSION,
            "review_id": review_id,
            "review_time_utc": datetime.now(timezone.utc).isoformat(),
            "round_number": round_number,
            "max_rounds": max_rounds,
            "status": status,
            "conclusion": STATUS_LABELS[status],
            "source": {
                "file_name": source.get("file_name"),
                "full_path": source.get("full_path"),
                "source_sha256": source.get("source_sha256"),
                "analysis_mode": source.get("analysis_mode"),
                "source_modified_by_workflow": False,
            },
            "geometry_summary": {
                "body_count": len(evidence.get("bodies", [])),
                "parting_surface": evidence.get("parting_surface", {}),
                "sharp_steel_summary": evidence.get("sharp_steel_summary", {}),
            },
            "rule_results": rule_results,
            "issues": issues,
            "unavailable_checks": [
                item for item in rule_results if item["status"] == "UNAVAILABLE"
            ],
            "change_tracking": change_tracking,
            "counts": counts,
            "repair_plan": self._build_repair_plan(issues),
            "next_action": self._build_next_action(status, round_number, max_rounds),
            "limitations": evidence.get("limitations", []),
        }
        return result

    def render_markdown(self, review: Dict) -> str:
        lines = [
            "# 分型面尖钢审查报告",
            "",
            "- Review ID：`{}`".format(review["review_id"]),
            "- 审查轮次：{} / {}".format(review["round_number"], review["max_rounds"]),
            "- 总体结论：**{}**".format(review["conclusion"]),
            "- 源文件哈希：`{}`".format(review["source"].get("source_sha256") or "unavailable"),
            "- 源文件状态：未被工作流修改",
            "",
            "## 确定性几何摘要",
            "",
        ]
        summary = review["geometry_summary"]["sharp_steel_summary"]
        lines.extend(
            [
                "- 原始候选：{}".format(summary.get("raw_candidate_count", 0)),
                "- 聚类候选：{}".format(summary.get("clustered_candidate_count", 0)),
                "- 确认几何风险：{}".format(
                    summary.get("confirmed_geometry_risk_count", 0)
                ),
                "- 报告候选：{}，省略：{}".format(
                    summary.get("reported_candidate_count", 0),
                    summary.get("omitted_candidate_count", 0),
                ),
                "",
                "> 边长与曲线最小半径只用于拓扑筛查，不等同于钢厚或真实钢料圆角。",
                "",
                "## 规则审查",
                "",
                "| 规则 | 状态 | 量测/依据 | 结论 |",
                "|---|---|---|---|",
            ]
        )
        for item in review["rule_results"]:
            lines.append(
                "| {} {} | {} | {} | {} |".format(
                    item["rule_id"],
                    _markdown_cell(item["name"]),
                    item["status"],
                    _markdown_cell(item["evidence"]),
                    _markdown_cell(item["message"]),
                )
            )

        lines.extend(["", "## 问题与建议", ""])
        if not review["issues"]:
            lines.append("未发现达到当前确定性门槛的尖钢几何候选。")
        for issue in review["issues"]:
            coordinate = issue["coordinate_approx"]
            lines.extend(
                [
                    "### {} [{}] {}".format(
                        issue["issue_id"], issue["severity"], issue["title"]
                    ),
                    "",
                    "- 坐标：X={:.3f}, Y={:.3f}, Z={:.3f} mm".format(
                        coordinate["x"], coordinate["y"], coordinate["z"]
                    ),
                    "- 几何量测：边长 {:.6f} mm，面夹角 {:.3f}°，窄面最小包围盒尺寸 {:.6f} mm".format(
                        issue["measurements"]["representative_edge_length_mm"],
                        issue["measurements"]["wedge_angle_deg"],
                        issue["measurements"]["min_narrow_face_dimension_mm"],
                    ),
                    "- 产品距离：{} mm".format(
                        _number(issue["measurements"].get("distance_to_product_mm"), 6)
                    ),
                    "- 工程判断：{}".format(issue["engineering_assessment"]),
                    "- 方案 A：{}".format(issue["recommendations"][0]["instruction"]),
                    "- 方案 B：{}".format(issue["recommendations"][1]["instruction"]),
                    "- 验证标准：{}".format("；".join(issue["verification_criteria"])),
                    "",
                ]
            )

        lines.extend(["## Loop 追踪", ""])
        tracking = review["change_tracking"]
        lines.extend(
            [
                "- 新增：{}".format(_join_ids(tracking["new_issue_ids"])),
                "- 改善：{}".format(_join_ids(tracking["improved_issue_ids"])),
                "- 未关闭：{}".format(_join_ids(tracking["remaining_issue_ids"])),
                "- 已关闭：{}".format(_join_ids(tracking["closed_issue_ids"])),
                "- 几何是否变化：{}".format(
                    "否" if tracking.get("same_source_hash") else "是或首轮"
                ),
                "",
                "## 数据边界",
                "",
            ]
        )
        unavailable = review["unavailable_checks"]
        if unavailable:
            for item in unavailable:
                lines.append("- {}：{}".format(item["rule_id"], item["message"]))
        else:
            lines.append("- 本轮规则所需量测均可用。")
        for limitation in review.get("limitations", []):
            lines.append("- {}".format(limitation))

        lines.extend(
            [
                "",
                "## 下一步",
                "",
                review["next_action"]["message"],
                "",
            ]
        )
        return "\n".join(lines)

    def _validate_evidence(self, evidence: Dict) -> None:
        if evidence.get("schema_version") != "2.0":
            raise ReviewEngineError("仅支持 geometry evidence schema_version 2.0")
        metadata = evidence.get("meta")
        if not isinstance(metadata, dict) or not metadata.get("source_sha256"):
            raise ReviewEngineError("几何证据缺少源文件 SHA-256")

    def _evaluate_rules(self, evidence: Dict) -> List[Dict]:
        return [
            self._parting_surface_complexity(evidence),
            self._maximum_contour(evidence),
            self._geometry_risk_rule(evidence),
            self._numeric_per_item_rule(evidence, "SS-001", "thickness_mm"),
            self._numeric_per_item_rule(evidence, "SS-002", "aspect_ratio"),
            self._numeric_per_item_rule(evidence, "SS-003", "edge_radius_mm"),
            self._undercut_rule(evidence),
        ]

    def _parting_surface_complexity(self, evidence: Dict) -> Dict:
        rule = self._rule("PL-001", "分型面复杂度")
        surface = evidence.get("parting_surface", {})
        if surface.get("measurement_status") == "unavailable":
            return self._result(rule, "UNAVAILABLE", "未识别到独立分型面 Sheet Body", "无法审查")
        flatness = surface.get("flatness_score")
        if surface.get("is_planar"):
            return self._result(rule, "PASS", "全部分型面均为平面", "满足平面规则")
        return self._result(
            rule,
            "WARN",
            "平面占比评分 {}/10，面数 {}".format(_number(flatness, 1), surface.get("face_count")),
            "分型面为复杂曲面，需确认必要性、加工与配模风险",
        )

    def _maximum_contour(self, evidence: Dict) -> Dict:
        rule = self._rule("PL-002", "最大轮廓分型位置")
        value = evidence.get("parting_line", {}).get("is_at_max_contour")
        if value is None:
            return self._result(
                rule,
                "UNAVAILABLE",
                "缺少开模方向与可见性分析",
                "不得推断分型面位于最大轮廓",
            )
        if value:
            return self._result(rule, "PASS", "is_at_max_contour=true", "满足规则")
        return self._result(
            rule, "ERROR", "is_at_max_contour=false", "存在倒扣或尖钢形成风险"
        )

    def _geometry_risk_rule(self, evidence: Dict) -> Dict:
        rule = self._rule("SS-GEO-001", "尖钢几何候选")
        summary = evidence.get("sharp_steel_summary", {})
        confirmed = int(summary.get("confirmed_geometry_risk_count", 0))
        candidates = int(summary.get("candidate_count", 0))
        detail = "确认风险 {}，待确认候选 {}".format(confirmed, candidates)
        if confirmed:
            return self._result(rule, "ERROR", detail, "存在满足三重几何门槛的尖钢风险")
        if candidates:
            return self._result(rule, "WARN", detail, "需由模具工程师确认局部钢料关系")
        return self._result(rule, "PASS", detail, "未命中当前尖钢候选门槛")

    def _numeric_per_item_rule(self, evidence: Dict, rule_id: str, field: str) -> Dict:
        rule = self._rule(rule_id, rule_id)
        values = [
            item.get(field)
            for item in evidence.get("sharp_steels", [])
            if item.get(field) is not None
        ]
        if not values:
            return self._result(
                rule,
                "UNAVAILABLE",
                "{} 无可信量测".format(field),
                "缺少独立型腔/型芯钢料实体，不得以边长代替",
            )
        operator = rule.get("operator")
        threshold = rule.get("threshold")
        failures = [value for value in values if not _compare(value, operator, threshold)]
        if not failures:
            return self._result(
                rule,
                "PASS",
                "{} 个量测均满足阈值 {}".format(len(values), threshold),
                "满足规则",
            )
        status = "ERROR" if rule.get("severity") == "ERROR" else "WARN"
        return self._result(
            rule,
            status,
            "{} 个不合格，最不利值 {}".format(len(failures), _worst(failures, operator)),
            rule.get("consequence", "不满足规则"),
        )

    def _undercut_rule(self, evidence: Dict) -> Dict:
        rule = self._rule("UC-001", "倒扣检测")
        analysis = evidence.get("undercut_analysis", {})
        if analysis.get("status") == "unavailable":
            return self._result(
                rule,
                "UNAVAILABLE",
                analysis.get("reason", "缺少开模方向"),
                "不得声明无倒扣",
            )
        undercuts = evidence.get("undercuts", [])
        if undercuts:
            return self._result(rule, "ERROR", "{} 个倒扣".format(len(undercuts)), "阻碍脱模")
        return self._result(rule, "PASS", "0 个倒扣", "满足规则")

    def _build_geometry_issues(self, evidence: Dict) -> List[Dict]:
        issues = []
        for risk in evidence.get("sharp_steels", []):
            classification = risk.get("classification")
            if classification not in ("confirmed_geometry_risk", "candidate"):
                continue
            severity = "ERROR" if classification == "confirmed_geometry_risk" else "WARN"
            coordinate = risk["coordinate_approx"]
            issue_id = "ISS-" + risk["fingerprint"].upper()
            assessment = (
                "确定性算法已确认微小边、可测夹角、窄非平面面且贴近产品，必须修改或提供反证。"
                if severity == "ERROR"
                else "确定性算法命中候选门槛；当前没有钢料实体，需工程师确认该处是否形成真实尖钢。"
            )
            issues.append(
                {
                    "issue_id": issue_id,
                    "fingerprint": risk["fingerprint"],
                    "severity": severity,
                    "classification": classification,
                    "title": "分型面局部尖钢几何风险",
                    "coordinate_approx": copy.deepcopy(coordinate),
                    "measurements": {
                        "representative_edge_length_mm": risk[
                            "representative_edge_length_mm"
                        ],
                        "wedge_angle_deg": risk["wedge_angle_deg"],
                        "min_narrow_face_dimension_mm": risk[
                            "min_narrow_face_dimension_mm"
                        ],
                        "distance_to_product_mm": risk.get("distance_to_product_mm"),
                        "thickness_mm": risk.get("thickness_mm"),
                        "height_mm": risk.get("height_mm"),
                        "aspect_ratio": risk.get("aspect_ratio"),
                        "true_edge_radius_mm": risk.get("edge_radius_mm"),
                        "curve_min_radius_mm": risk.get("curve_min_radius_mm"),
                    },
                    "geometry_evidence": copy.deepcopy(risk.get("evidence", {})),
                    "engineering_assessment": assessment,
                    "recommendations": self._recommendations(risk),
                    "verification_criteria": self._verification_criteria(risk),
                }
            )
        return issues

    def _recommendations(self, risk: Dict) -> List[Dict]:
        coordinate = risk["coordinate_approx"]
        location = "X={:.3f}, Y={:.3f}, Z={:.3f} mm".format(
            coordinate["x"], coordinate["y"], coordinate["z"]
        )
        return [
            {
                "priority": "A",
                "type": "parting_surface_local_rework",
                "instruction": (
                    "在 {} 周围优先重构或平顺分型面，消除微小边和狭窄碎面；"
                    "修改量必须由相邻产品面与钢料实体重新量测后确定，禁止凭当前边长推算。"
                ).format(location),
                "parameters": {
                    "inspection_radius_mm": self.policy.get("cluster_radius_mm", 2.0),
                    "target_min_steel_thickness_mm": 2.0,
                    "target_max_aspect_ratio": 3.0,
                    "target_min_true_radius_mm": 0.5,
                },
            },
            {
                "priority": "B",
                "type": "local_insert",
                "instruction": (
                    "若产品功能不允许移动分型面，在该坐标建立独立镶件；"
                    "镶件外形、锁固和材料必须结合真实钢料实体、寿命和冷却重新设计。"
                ),
                "parameters": {
                    "target_min_insert_ligament_mm": 2.5,
                    "target_min_true_radius_mm": 0.5,
                    "fit_and_material": "需模具工程师按寿命与加工能力确认",
                },
            },
        ]

    def _verification_criteria(self, risk: Dict) -> List[str]:
        criteria = [
            "复跑后该坐标 {} mm 聚类范围内不再出现 confirmed_geometry_risk".format(
                self.policy.get("cluster_radius_mm", 2.0)
            ),
            "有型腔/型芯钢料实体时，真实最小钢厚 ≥ 2.0 mm",
            "真实钢料高度/厚度 ≤ 3.0",
            "真实应力集中圆角 ≥ 0.5 mm",
        ]
        if risk.get("classification") == "candidate":
            criteria.insert(0, "人工确认该局部是否构成承压尖钢，并记录依据")
        return criteria

    def _track_changes(
        self,
        current_issues: List[Dict],
        previous_review: Optional[Dict],
        current_source_hash: str,
    ) -> Tuple[List[Dict], Dict]:
        if not previous_review:
            identifiers = [item["issue_id"] for item in current_issues]
            return current_issues, {
                "same_source_hash": False,
                "new_issue_ids": identifiers,
                "improved_issue_ids": [],
                "remaining_issue_ids": identifiers,
                "closed_issue_ids": [],
                "regressed_issue_ids": [],
                "comparisons": [],
            }

        previous_issues = previous_review.get("issues", [])
        unmatched = set(range(len(current_issues)))
        comparisons = []
        new_ids = []
        improved_ids = []
        remaining_ids = []
        closed_ids = []
        regressed_ids = []

        for previous in previous_issues:
            match_index = _find_issue_match(previous, current_issues, unmatched)
            if match_index is None:
                closed_ids.append(previous["issue_id"])
                comparisons.append(
                    {
                        "issue_id": previous["issue_id"],
                        "previous": previous.get("severity"),
                        "current": "CLOSED",
                        "change": "closed",
                    }
                )
                continue
            unmatched.remove(match_index)
            current = current_issues[match_index]
            current["issue_id"] = previous["issue_id"]
            current_rank = _severity_rank(current.get("severity"))
            previous_rank = _severity_rank(previous.get("severity"))
            if current_rank < previous_rank:
                change = "improved"
                improved_ids.append(current["issue_id"])
            elif current_rank > previous_rank:
                change = "regressed"
                regressed_ids.append(current["issue_id"])
            else:
                change = "remaining"
            remaining_ids.append(current["issue_id"])
            comparisons.append(
                {
                    "issue_id": current["issue_id"],
                    "previous": previous.get("severity"),
                    "current": current.get("severity"),
                    "change": change,
                    "coordinate_shift_mm": _coordinate_distance(
                        previous.get("coordinate_approx"), current.get("coordinate_approx")
                    ),
                }
            )

        for index in sorted(unmatched):
            issue = current_issues[index]
            new_ids.append(issue["issue_id"])
            remaining_ids.append(issue["issue_id"])
            comparisons.append(
                {
                    "issue_id": issue["issue_id"],
                    "previous": None,
                    "current": issue.get("severity"),
                    "change": "new",
                }
            )

        previous_hash = previous_review.get("source", {}).get("source_sha256")
        same_hash = previous_hash == current_source_hash
        return current_issues, {
            "same_source_hash": same_hash,
            "new_issue_ids": new_ids,
            "improved_issue_ids": improved_ids,
            "remaining_issue_ids": remaining_ids,
            "closed_issue_ids": closed_ids,
            "regressed_issue_ids": regressed_ids,
            "comparisons": comparisons,
        }

    def _build_counts(self, rule_results: List[Dict], issues: List[Dict]) -> Dict:
        return {
            "rule_error": sum(1 for item in rule_results if item["status"] == "ERROR"),
            "rule_warn": sum(1 for item in rule_results if item["status"] == "WARN"),
            "rule_unavailable": sum(
                1 for item in rule_results if item["status"] == "UNAVAILABLE"
            ),
            "issue_error": sum(1 for item in issues if item["severity"] == "ERROR"),
            "issue_warn": sum(1 for item in issues if item["severity"] == "WARN"),
        }

    def _determine_status(self, counts: Dict) -> str:
        if counts["issue_error"] or counts["rule_error"]:
            return "not_passed"
        if counts["issue_warn"] or counts["rule_warn"] or counts["rule_unavailable"]:
            return "conditional"
        return "passed"

    def _build_repair_plan(self, issues: List[Dict]) -> Dict:
        return {
            "status": "manual_or_licensed_nx_modifier_required" if issues else "not_required",
            "source_file_must_remain_unchanged": True,
            "may_prepare_working_copy": bool(issues),
            "may_label_output_as_modified": False,
            "reason": (
                "当前工作流只生成量测证据和修改计划；合法 NX Headless 修改成功并复审后，"
                "才可交付修改版 .prt。"
                if issues
                else "未生成修改任务。"
            ),
            "operations": [
                {
                    "issue_id": issue["issue_id"],
                    "coordinate_approx": issue["coordinate_approx"],
                    "preferred_action": issue["recommendations"][0],
                    "fallback_action": issue["recommendations"][1],
                }
                for issue in issues
            ],
        }

    def _build_next_action(self, status: str, round_number: int, max_rounds: int) -> Dict:
        if status == "passed":
            return {"action": "finish", "message": "审查闭环，可输出最终报告。"}
        if status == "stopped_incomplete":
            return {
                "action": "human_review",
                "message": "已达到最大轮次，停止自动 Loop，遗留项转人工模具评审。",
            }
        return {
            "action": "modify_copy_and_recheck",
            "message": (
                "仅修改工作副本；完成后把新 .prt 作为同一 Session 的下一轮输入，"
                "工作流会自动匹配、关闭或升级问题。"
            ),
        }

    def _rule(self, rule_id: str, fallback_name: str) -> Dict:
        rule = copy.deepcopy(self.rules.get(rule_id, {}))
        rule.setdefault("id", rule_id)
        rule.setdefault("name", fallback_name)
        return rule

    @staticmethod
    def _result(rule: Dict, status: str, evidence: str, message: str) -> Dict:
        return {
            "rule_id": rule["id"],
            "name": rule["name"],
            "status": status,
            "configured_severity": rule.get("severity"),
            "evidence": evidence,
            "message": message,
            "consequence": rule.get("consequence"),
        }


def _compare(value: float, operator: str, threshold: float) -> bool:
    if operator == ">=":
        return value >= threshold
    if operator == "<=":
        return value <= threshold
    if operator == "==":
        return value == threshold
    raise ReviewEngineError("不支持的规则操作符: " + str(operator))


def _worst(values: Iterable[float], operator: str) -> float:
    return min(values) if operator == ">=" else max(values)


def _find_issue_match(
    previous: Dict, current_issues: List[Dict], unmatched: set
) -> Optional[int]:
    previous_fingerprint = previous.get("fingerprint")
    for index in unmatched:
        if previous_fingerprint and current_issues[index].get("fingerprint") == previous_fingerprint:
            return index
    previous_coordinate = previous.get("coordinate_approx")
    nearest_index = None
    nearest_distance = math.inf
    for index in unmatched:
        distance = _coordinate_distance(
            previous_coordinate, current_issues[index].get("coordinate_approx")
        )
        if distance is not None and distance <= 3.0 and distance < nearest_distance:
            nearest_index = index
            nearest_distance = distance
    return nearest_index


def _coordinate_distance(first: Optional[Dict], second: Optional[Dict]) -> Optional[float]:
    if not first or not second:
        return None
    return math.sqrt(sum((first[axis] - second[axis]) ** 2 for axis in ("x", "y", "z")))


def _severity_rank(severity: Optional[str]) -> int:
    return {"ERROR": 2, "WARN": 1}.get(severity or "", 0)


def _number(value: Optional[float], digits: int) -> str:
    if value is None:
        return "unavailable"
    return ("{0:." + str(digits) + "f}").format(value)


def _markdown_cell(value: object) -> str:
    return str(value).replace("|", "\\|").replace("\n", " ")


def _join_ids(values: List[str]) -> str:
    return ", ".join(values) if values else "无"


def load_json(path: Path) -> Dict:
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as error:
        raise ReviewEngineError("无法读取 JSON {}: {}".format(path, error))


def main() -> int:
    parser = argparse.ArgumentParser(description="从确定性几何证据生成尖钢审查结果")
    parser.add_argument("evidence", type=Path)
    parser.add_argument("--previous", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--markdown", type=Path)
    parser.add_argument("--round", type=int, default=1)
    parser.add_argument("--max-rounds", type=int, default=5)
    args = parser.parse_args()

    evidence = load_json(args.evidence)
    previous = load_json(args.previous) if args.previous else None
    engine = ReviewEngine()
    result = engine.review(
        evidence,
        previous_review=previous,
        round_number=args.round,
        max_rounds=args.max_rounds,
    )
    output_path = args.output or args.evidence.with_name("review_result.json")
    markdown_path = args.markdown or output_path.with_suffix(".md")
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(
        json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    markdown_path.write_text(engine.render_markdown(result), encoding="utf-8")
    print("review_json=" + str(output_path.resolve()))
    print("review_markdown=" + str(markdown_path.resolve()))
    print("status=" + result["status"])
    return 1 if result["status"] in ("not_passed", "stopped_incomplete") else 0


if __name__ == "__main__":
    raise SystemExit(main())
