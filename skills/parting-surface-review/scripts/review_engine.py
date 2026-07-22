# -*- coding: utf-8 -*-
"""
=============================================================================
分型面尖钢审查引擎 (Review Engine)
=============================================================================
用途：接收 NX Journal 提取的 JSON 数据，执行规则审查、工程推理、生成修改方案
运行：python review_engine.py <input.json>
      或在代码中 import review_engine
=============================================================================
"""

import json
import sys
import os
from datetime import datetime
from typing import Dict, List, Any, Optional, Tuple
from dataclasses import dataclass, field, asdict


# ============================================================================
# 数据结构
# ============================================================================

@dataclass
class ReviewResult:
    """单条规则的审查结果"""
    rule_id: str
    rule_name: str
    severity: str          # ERROR / WARN
    passed: bool
    actual_value: Any
    threshold: Any
    detail: str
    affected_items: List[str] = field(default_factory=list)


@dataclass
class Issue:
    """发现的问题"""
    issue_id: str
    title: str
    severity: str
    rule_ids: List[str]
    position: str
    coordinates: Dict[str, float]
    current_values: Dict[str, Any]
    required_values: Dict[str, Any]
    category: str          # sharp_steel / parting_line / undercut


@dataclass
class Solution:
    """修改方案"""
    solution_id: str
    issue_id: str
    solution_type: str     # A / B
    priority: str          # 推荐 / 备选
    description: str
    parameters: Dict[str, Any]
    nx_operations: List[str]
    verification: Dict[str, Any]
    cost_impact: str


@dataclass
class ReviewTracker:
    """审查状态追踪器"""
    current_round: int
    max_rounds: int
    overall_status: str    # 通过 / 不通过
    errors_this_round: int
    warns_this_round: int
    closed_issues: List[str]
    remaining_issues: List[str]
    pending_actions: List[str]
    terminated: bool = False
    terminate_reason: str = ""


# ============================================================================
# 规则引擎
# ============================================================================

class ReviewEngine:
    """分型面尖钢审查引擎"""

    def __init__(self, rules_path: Optional[str] = None):
        """初始化引擎，加载规则"""
        if rules_path:
            self.rules = self._load_rules(rules_path)
        else:
            self.rules = self._default_rules()

        self.material_factors = {
            "ABS": 1.0, "PC": 1.2, "POM": 1.5,
            "PA": 2.0, "PA+GF": 3.5, "PA6+GF30": 3.5,
            "PA66+GF30": 4.0, "PP": 1.0, "PP+GF": 3.0,
            "PP+GF30": 3.5, "PBT+GF30": 3.5, "PPS+GF40": 5.0,
            "PEEK+GF30": 4.0, "PEI": 2.0, "PMMA": 1.0,
            "LCP": 1.5, "DEFAULT": 1.0
        }

    def _load_rules(self, path: str) -> List[Dict]:
        """从 JSON 文件加载规则"""
        with open(path, 'r', encoding='utf-8-sig') as f:
            config = json.load(f)
        return config.get("rules", [])

    def _default_rules(self) -> List[Dict]:
        """默认审查规则"""
        return [
            {
                "id": "PL-001", "name": "分型面平直度",
                "field": "parting_line.flatness_score",
                "operator": ">=", "threshold": 8,
                "severity": "WARN", "category": "parting_line",
                "consequence": "加工困难，配模精度差"
            },
            {
                "id": "PL-002", "name": "分型面位置",
                "field": "parting_line.is_at_max_contour",
                "operator": "==", "threshold": True,
                "severity": "ERROR", "category": "parting_line",
                "consequence": "产生倒扣，拉伤产品，或形成尖钢"
            },
            {
                "id": "SS-001", "name": "尖钢最小壁厚",
                "field": "sharp_steels[*].thickness_mm",
                "operator": ">=", "threshold": 2.0,
                "severity": "ERROR", "category": "sharp_steel",
                "consequence": "崩裂，模具寿命急剧下降",
                "per_item": True
            },
            {
                "id": "SS-002", "name": "钢料细长比",
                "field": "sharp_steels[*].aspect_ratio",
                "operator": "<=", "threshold": 3.0,
                "severity": "ERROR", "category": "sharp_steel",
                "consequence": "强度不足，受压变形",
                "per_item": True
            },
            {
                "id": "SS-003", "name": "尖钢边缘R角",
                "field": "sharp_steels[*].edge_radius_mm",
                "operator": ">=", "threshold": 0.5,
                "severity": "WARN", "category": "sharp_steel",
                "consequence": "应力集中，易崩缺",
                "per_item": True
            },
            {
                "id": "UC-001", "name": "倒扣检测",
                "field": "undercuts",
                "operator": "length_zero", "threshold": 0,
                "severity": "ERROR", "category": "undercut",
                "consequence": "无法正常脱模"
            }
        ]

    # ========================================================================
    # 核心审查流程
    # ========================================================================

    def review(self, data: Dict, previous_data: Optional[Dict] = None,
               round_num: int = 1, max_rounds: int = 5) -> Dict:
        """
        执行完整审查流程

        Args:
            data: NX Journal 提取的 JSON 数据
            previous_data: 上一轮的数据（用于对比）
            round_num: 当前轮次
            max_rounds: 最大轮次

        Returns:
            包含所有审查结果的字典
        """
        # Step 1: 规则审查
        rule_results = self._run_rules(data)
        errors = [r for r in rule_results if r.severity == "ERROR" and not r.passed]
        warns = [r for r in rule_results if r.severity == "WARN" and not r.passed]

        # Step 2: 工程推理
        engineering_notes = self._engineering_reasoning(data, rule_results)

        # Step 3: 问题清单与修改方案
        issues = self._build_issues(data, errors + warns)
        solutions = self._generate_solutions(issues, data)

        # Step 4: 追踪器
        overall_status = "通过" if (len(errors) == 0 and len(warns) <= 1) else "不通过"
        tracker = ReviewTracker(
            current_round=round_num,
            max_rounds=max_rounds,
            overall_status=overall_status,
            errors_this_round=len(errors),
            warns_this_round=len(warns),
            closed_issues=self._find_closed(previous_data, data, issues) if previous_data else [],
            remaining_issues=[i.issue_id for i in issues],
            pending_actions=self._build_pending_actions(issues, solutions)
        )

        # 检查是否强制终止
        if round_num >= max_rounds and overall_status != "通过":
            tracker.terminated = True
            tracker.terminate_reason = "达到最大轮次 {} 轮".format(max_rounds)

        # 组装输出
        return {
            "rule_results": [asdict(r) for r in rule_results],
            "engineering_notes": engineering_notes,
            "issues": [asdict(i) for i in issues],
            "solutions": [asdict(s) for s in solutions],
            "tracker": asdict(tracker),
            "is_pass": overall_status == "通过"
        }

    # ========================================================================
    # Step 1: 规则引擎
    # ========================================================================

    def _run_rules(self, data: Dict) -> List[ReviewResult]:
        """运行所有审查规则"""
        results = []

        for rule in self.rules:
            rule_id = rule["id"]
            is_per_item = rule.get("per_item", False)

            if is_per_item:
                item_results = self._check_per_item_rule(data, rule)
                results.extend(item_results)
            else:
                result = self._check_single_rule(data, rule)
                results.append(result)

        return results

    def _check_single_rule(self, data: Dict, rule: Dict) -> ReviewResult:
        """检查单条规则"""
        field_path = rule["field"]
        operator = rule["operator"]
        threshold = rule["threshold"]

        # 提取字段值
        value = self._get_field(data, field_path)

        # 执行比较
        passed = self._compare(value, operator, threshold)

        detail = self._build_detail(rule, value, passed)
        return ReviewResult(
            rule_id=rule["id"],
            rule_name=rule["name"],
            severity=rule["severity"],
            passed=passed,
            actual_value=value,
            threshold=threshold,
            detail=detail
        )

    def _check_per_item_rule(self, data: Dict, rule: Dict) -> List[ReviewResult]:
        """检查数组中的逐项规则"""
        field_path = rule["field"]
        operator = rule["operator"]
        threshold = rule["threshold"]
        results = []

        # 解析路径：sharp_steels[*].thickness_mm
        array_path = field_path.split("[*]")[0]
        item_field = field_path.split("[*].")[1] if "[*]." in field_path else field_path.split("[*]")[1].lstrip(".")

        items = self._get_field(data, array_path) or []

        for item in items:
            value = item.get(item_field, None)
            item_id = item.get("id", "UNKNOWN")
            passed = self._compare(value, operator, threshold)
            detail = "{}: {} = {} (阈值: {} {})".format(
                item_id, item_field, value,
                self._op_symbol(operator), threshold
            )

            results.append(ReviewResult(
                rule_id=rule["id"],
                rule_name=rule["name"],
                severity=rule["severity"],
                passed=passed,
                actual_value=value,
                threshold=threshold,
                detail=detail,
                affected_items=[item_id]
            ))

        # 如果没有找到任何项，视为通过
        if not items:
            results.append(ReviewResult(
                rule_id=rule["id"],
                rule_name=rule["name"],
                severity=rule["severity"],
                passed=True,
                actual_value="无检测项",
                threshold=threshold,
                detail="未找到可检测的尖钢特征"
            ))

        return results

    def _get_field(self, data: Dict, path: str) -> Any:
        """从嵌套字典中按路径获取字段值"""
        keys = path.split(".")
        value = data
        for key in keys:
            if key.endswith("[*]"):
                key = key[:-3]
            if isinstance(value, dict):
                value = value.get(key)
            else:
                return None
            if value is None:
                return None
        return value

    def _compare(self, value: Any, operator: str, threshold: Any) -> bool:
        """执行比较操作"""
        if value is None:
            return False

        if operator == ">=":
            return value >= threshold
        elif operator == "<=":
            return value <= threshold
        elif operator == "==":
            return value == threshold
        elif operator == "!=":
            return value != threshold
        elif operator == ">":
            return value > threshold
        elif operator == "<":
            return value < threshold
        elif operator == "length_zero":
            return len(value) == 0 if isinstance(value, (list, str)) else False
        return False

    def _op_symbol(self, operator: str) -> str:
        """操作符显示符号"""
        symbols = {
            ">=": "≥", "<=": "≤", "==": "=",
            "!=": "≠", ">": ">", "<": "<",
            "length_zero": "空"
        }
        return symbols.get(operator, operator)

    def _build_detail(self, rule: Dict, value: Any, passed: bool) -> str:
        """构建审查结果描述"""
        status = "✅" if passed else ("❌" if rule["severity"] == "ERROR" else "⚠️")
        op = self._op_symbol(rule["operator"])
        return "{} {}: 实际值={} {} 阈值={} → {}".format(
            status, rule["name"], value, op, rule["threshold"],
            "通过" if passed else rule.get("consequence", "不通过")
        )

    # ========================================================================
    # Step 2: 工程推理
    # ========================================================================

    def _engineering_reasoning(self, data: Dict,
                                rule_results: List[ReviewResult]) -> List[Dict]:
        """工程推理：基于规则结果和工艺经验进行软判断"""
        notes = []
        material = data.get("product", {}).get("material", "UNKNOWN")
        wear_factor = self.material_factors.get(material, 1.0)

        # 1. 高压区尖钢分析
        sharp_steels = data.get("sharp_steels", [])
        high_pressure_ss = [s for s in sharp_steels if s.get("is_in_high_pressure_zone")]
        if high_pressure_ss:
            notes.append({
                "type": "高压区尖钢",
                "severity": "WARN",
                "detail": "检测到 {} 处尖钢位于高压区（浇口附近/填充末端），{} 注塑压力下风险显著增加".format(
                    len(high_pressure_ss), material
                ),
                "recommendation": "高压区尖钢建议最小壁厚提升至 2.5mm，长径比 ≤ 2:1"
            })

        # 2. 材料敏感度分析
        if wear_factor >= 3.0:
            notes.append({
                "type": "玻纤材料磨损风险",
                "severity": "WARN",
                "detail": "材料 {} 含玻纤增强，磨损系数 {:.1f}x，尖钢磨损速度比 ABS 快 {:.0f}~{:.0f} 倍".format(
                    material, wear_factor, wear_factor, wear_factor * 1.5
                ),
                "recommendation": "建议尖钢区域使用 H13 (HRC 48~52) 或 SKD61 镶件，表面氮化处理"
            })

        # 3. 分型面复杂度成本分析
        pl = data.get("parting_line", {})
        flatness = pl.get("flatness_score", 10)
        shape_type = pl.get("shape_type", "flat")
        if flatness < 6:
            multiplier = 2.0 if shape_type == "wavy" else 3.0
            notes.append({
                "type": "分型面加工成本",
                "severity": "INFO",
                "detail": "分型面类型为 {} (平直度 {}/10)，加工费约为平面的 {:.0f} 倍".format(
                    shape_type, flatness, multiplier
                ),
                "recommendation": "评估是否可以通过调整产品结构简化分型面"
            })

        # 4. 模具寿命预估修正
        base_life = data.get("mold", {}).get("expected_shot_life_k", 10) * 1000
        ss_issues = [r for r in rule_results if r.rule_id.startswith("SS-") and not r.passed]
        if ss_issues:
            life_reduction = len(ss_issues) * 0.3
            adjusted_life = base_life * (1 - life_reduction) / 1000
            notes.append({
                "type": "模具寿命预估",
                "severity": "WARN" if life_reduction > 0.3 else "INFO",
                "detail": "存在 {} 个尖钢问题，预估模具寿命降至 {:.1f}K 模次（原 {:.0f}K）".format(
                    len(ss_issues), adjusted_life, base_life / 1000
                ),
                "recommendation": "修复所有尖钢问题后，寿命可恢复至原始预估的 90% 以上"
            })

        return notes

    # ========================================================================
    # Step 3: 问题清单与修改方案
    # ========================================================================

    def _build_issues(self, data: Dict,
                       failed_rules: List[ReviewResult]) -> List[Issue]:
        """构建问题清单"""
        issues = []
        ss_counter = 0
        uc_counter = 0
        pl_counter = 0

        for result in failed_rules:
            rule_id = result.rule_id

            if rule_id.startswith("SS-"):
                # 尖钢问题
                for item_id in result.affected_items:
                    ss = self._find_sharp_steel(data, item_id)
                    if ss:
                        ss_counter += 1
                        issues.append(Issue(
                            issue_id="ISS-{:03d}".format(ss_counter),
                            title="分型面尖钢 — {}".format(ss.get("position", "未知位置")),
                            severity=result.severity,
                            rule_ids=[rule_id],
                            position=ss.get("position", ""),
                            coordinates=ss.get("coordinate_approx", {}),
                            current_values={
                                "thickness_mm": ss.get("thickness_mm"),
                                "height_mm": ss.get("height_mm"),
                                "aspect_ratio": ss.get("aspect_ratio"),
                                "edge_radius_mm": ss.get("edge_radius_mm")
                            },
                            required_values={
                                "thickness_mm": "≥ 2.0",
                                "aspect_ratio": "≤ 3.0",
                                "edge_radius_mm": "≥ 0.5"
                            },
                            category="sharp_steel"
                        ))

            elif rule_id.startswith("PL-"):
                # 分型面问题
                pl_counter += 1
                pl = data.get("parting_line", {})
                issues.append(Issue(
                    issue_id="ISS-PL-{:03d}".format(pl_counter),
                    title="分型面问题 — {}".format(result.rule_name),
                    severity=result.severity,
                    rule_ids=[rule_id],
                    position="分型面",
                    coordinates={"y": pl.get("coordinate_y_mm", 0)},
                    current_values={
                        "flatness_score": pl.get("flatness_score"),
                        "is_at_max_contour": pl.get("is_at_max_contour")
                    },
                    required_values={
                        "flatness_score": "≥ 8",
                        "is_at_max_contour": True
                    },
                    category="parting_line"
                ))

            elif rule_id.startswith("UC-"):
                # 倒扣问题
                for uc in data.get("undercuts", []):
                    uc_counter += 1
                    issues.append(Issue(
                        issue_id="ISS-UC-{:03d}".format(uc_counter),
                        title="倒扣 — {}".format(uc.get("position", "未知位置")),
                        severity=result.severity,
                        rule_ids=[rule_id],
                        position=uc.get("position", ""),
                        coordinates={},
                        current_values={
                            "depth_mm": uc.get("depth_mm"),
                            "direction": uc.get("direction"),
                            "requires_slider": uc.get("requires_slider")
                        },
                        required_values={
                            "undercuts": "清空所有倒扣"
                        },
                        category="undercut"
                    ))

        # 按严重程度排序（ERROR 在前）
        issues.sort(key=lambda i: (0 if i.severity == "ERROR" else 1, i.issue_id))
        return issues

    def _generate_solutions(self, issues: List[Issue],
                             data: Dict) -> List[Solution]:
        """为每个问题生成方案A和方案B"""
        solutions = []
        pl = data.get("parting_line", {})
        pl_y = pl.get("coordinate_y_mm", 0)
        product = data.get("product", {})

        for issue in issues:
            sol_id = 0

            if issue.category == "sharp_steel":
                # 方案A：调整分型面
                thickness = issue.current_values.get("thickness_mm", 1.0)
                height = issue.current_values.get("height_mm", 1.0)
                target_y = pl_y + (2.0 - thickness) * 2.0 if thickness < 2.0 else pl_y
                new_thickness = thickness + abs(target_y - pl_y) * 0.5

                sol_id += 1
                solutions.append(Solution(
                    solution_id="{}-A".format(issue.issue_id),
                    issue_id=issue.issue_id,
                    solution_type="A",
                    priority="推荐",
                    description="调整分型面位置 —— 零额外机构成本",
                    parameters={
                        "分型面上移距离_mm": round(target_y - pl_y, 1),
                        "新分型面坐标_Y_mm": round(target_y, 1),
                        "修改后钢料厚度_mm": round(new_thickness, 1),
                        "修改后细长比": round(height / new_thickness, 1)
                    },
                    nx_operations=[
                        "建模 → 曲面 → 有界平面，选择产品顶面最大轮廓线",
                        "注塑模向导 → 分型 → 编辑分型面 → 替换为新建平面",
                        "重新计算型腔/型芯区域"
                    ],
                    verification={
                        "检查项": "该处 thickness_mm",
                        "目标值": "≥ 2.0 mm"
                    },
                    cost_impact="零额外机构成本"
                ))

                # 方案B：局部镶件
                sol_id += 1
                solutions.append(Solution(
                    solution_id="{}-B".format(issue.issue_id),
                    issue_id=issue.issue_id,
                    solution_type="B",
                    priority="备选",
                    description="局部镶件 —— 当分型面不可移动时采用",
                    parameters={
                        "镶件材料": "H13 (HRC 48~52)",
                        "配合间隙_mm": 0.015,
                        "镶件边缘最小厚度_mm": 2.5
                    },
                    nx_operations=[
                        "建模 → 拉伸，在尖钢区域绘制镶件轮廓",
                        "布尔求差（从模仁减去）",
                        "新建组件 → 添加镶件零件"
                    ],
                    verification={
                        "检查项": "镶件边缘最小厚度",
                        "目标值": "≥ 2.5 mm"
                    },
                    cost_impact="镶件加工费 + 装配工时"
                ))

            elif issue.category == "parting_line":
                # 分型面问题方案
                solutions.append(Solution(
                    solution_id="{}-A".format(issue.issue_id),
                    issue_id=issue.issue_id,
                    solution_type="A",
                    priority="推荐",
                    description="重新评估分型面设计",
                    parameters={
                        "建议分型面平直度": "≥ 8/10",
                        "分型面应位于": "产品最大轮廓处"
                    },
                    nx_operations=[
                        "注塑模向导 → 分型 → 自动分型面",
                        "手动调整分型线至最大轮廓",
                        "重建分型面并验证"
                    ],
                    verification={
                        "检查项": "parting_line.flatness_score / is_at_max_contour",
                        "目标值": "≥ 8 / true"
                    },
                    cost_impact="设计阶段调整，成本可忽略"
                ))

            elif issue.category == "undercut":
                # 倒扣问题方案
                depth = issue.current_values.get("depth_mm", 1.0)
                direction = issue.current_values.get("direction", "horizontal")

                solutions.append(Solution(
                    solution_id="{}-A".format(issue.issue_id),
                    issue_id=issue.issue_id,
                    solution_type="A",
                    priority="推荐",
                    description="添加滑块/斜顶机构解除倒扣",
                    parameters={
                        "倒扣深度_mm": depth,
                        "倒扣方向": direction,
                        "滑块行程_mm": round(depth + 3, 1),
                        "滑块角度_deg": 12
                    },
                    nx_operations=[
                        "注塑模向导 → 滑块/斜顶库",
                        "选择对应方向的标准滑块",
                        "调整滑块行程和角度参数"
                    ],
                    verification={
                        "检查项": "undercuts 数组",
                        "目标值": "应为空"
                    },
                    cost_impact="模具成本 +15~25%"
                ))

        return solutions

    def _find_sharp_steel(self, data: Dict, ss_id: str) -> Optional[Dict]:
        """根据 ID 查找尖钢数据"""
        for ss in data.get("sharp_steels", []):
            if ss.get("id") == ss_id:
                return ss
        return None

    # ========================================================================
    # Step 4: 追踪器辅助方法
    # ========================================================================

    def _find_closed(self, previous_data: Optional[Dict],
                      current_data: Dict, issues: List[Issue]) -> List[str]:
        """找出哪些问题上轮有、本轮已修复"""
        if not previous_data:
            return []

        closed = []
        current_issue_ids = {i.issue_id for i in issues}

        # 检查上一轮的尖钢是否已消除
        prev_ss_ids = {s["id"] for s in previous_data.get("sharp_steels", [])}
        curr_ss_ids = {s["id"] for s in current_data.get("sharp_steels", [])}

        for prev_id in prev_ss_ids - curr_ss_ids:
            closed.append("ISS-{}".format(prev_id.replace("SS-", "")))

        return closed

    def _build_pending_actions(self, issues: List[Issue],
                                solutions: List[Solution]) -> List[str]:
        """构建用户待执行动作列表"""
        actions = []

        for issue in issues:
            sol_a = next((s for s in solutions
                         if s.issue_id == issue.issue_id and s.solution_type == "A"), None)
            if sol_a:
                if issue.category == "sharp_steel":
                    dy = sol_a.parameters.get("分型面上移距离_mm", 0)
                    if dy != 0:
                        actions.append(
                            "{} → 方案A：移动分型面 ({:.1f}mm) [{}]".format(
                                issue.issue_id, dy, issue.title
                            )
                        )
                elif issue.category == "undercut":
                    actions.append(
                        "{} → 方案A：添加滑块机构 [{}]".format(
                            issue.issue_id, issue.title
                        )
                    )
                else:
                    actions.append(
                        "{} → 方案A：{} [{}]".format(
                            issue.issue_id, sol_a.description, issue.title
                        )
                    )

        actions.append("修改完成后重新运行 NX Journal 脚本")
        actions.append("将新 JSON 贴回继续审查")
        return actions

    # ========================================================================
    # 最终报告
    # ========================================================================

    def generate_final_report(self, history: List[Dict]) -> str:
        """生成最终审查报告"""
        if not history:
            return "无审查历史数据"

        last = history[-1]
        tracker = last.get("tracker", {})
        total_rounds = len(history)

        # 汇总所有轮次的问题
        all_errors = 0
        all_warns = 0
        all_issues = []

        for h in history:
            all_errors += h.get("tracker", {}).get("errors_this_round", 0)
            all_warns += h.get("tracker", {}).get("warns_this_round", 0)
            for issue in h.get("issues", []):
                all_issues.append(issue)

        closed_issues = tracker.get("closed_issues", [])
        remaining = tracker.get("remaining_issues", [])

        report = []
        report.append("=" * 60)
        report.append("      分型面尖钢审查最终报告")
        report.append("      Review ID: REV-{}-{}".format(
            datetime.now().strftime("%Y%m%d"), total_rounds
        ))
        report.append("=" * 60)
        report.append("")
        report.append("一、审查统计")
        report.append("   • 总轮次：{} 轮".format(total_rounds))
        report.append("   • 累计发现问题：{} 个 ERROR，{} 个 WARN".format(all_errors, all_warns))
        report.append("   • 成功关闭：{} 个".format(len(closed_issues)))
        report.append("   • 遗留问题：{} 个".format(len(remaining)))
        report.append("")

        # 状态评级
        status = tracker.get("overall_status", "未知")
        report.append("三、最终设计状态评级")
        if status == "通过":
            report.append("   • [✓] 通过（ERROR=0, WARN≤1）")
        elif len(remaining) > 0 and all(e.get("severity") != "ERROR" or e.get("issue_id") not in remaining for e in all_issues if e.get("issue_id") in remaining):
            report.append("   • [✓] 有条件通过（遗留WARN，需人工确认）")
        else:
            report.append("   • [✗] 不通过（遗留ERROR，不建议开模）")
        report.append("")

        # 遗留风险
        if remaining:
            report.append("四、遗留风险说明")
            for issue in all_issues:
                if issue.get("issue_id") in remaining:
                    report.append("   • {} [{}] {}: {}".format(
                        issue.get("issue_id", ""),
                        issue.get("severity", ""),
                        issue.get("title", ""),
                        issue.get("current_values", {})
                    ))
            report.append("")

        report.append("=" * 60)
        return "\n".join(report)


# ============================================================================
# CLI 入口
# ============================================================================

def main_cli():
    """命令行入口：python review_engine.py <input.json>"""
    if len(sys.argv) < 2:
        print("用法: python review_engine.py <input.json>")
        print("      python review_engine.py <input.json> <previous.json>")
        sys.exit(1)

    input_path = sys.argv[1]
    previous_path = sys.argv[2] if len(sys.argv) > 2 else None

    with open(input_path, 'r', encoding='utf-8-sig') as f:
        data = json.load(f)

    previous_data = None
    if previous_path:
        with open(previous_path, 'r', encoding='utf-8-sig') as f:
            previous_data = json.load(f)

    engine = ReviewEngine()
    result = engine.review(data, previous_data)

    # 输出
    output = {
        "input_file": input_path,
        "review_time": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        **result
    }

    out_path = input_path.replace(".json", "_reviewed.json")
    with open(out_path, 'w', encoding='utf-8-sig') as f:
        json.dump(output, f, ensure_ascii=False, indent=2)

    print("Review done! Saved to: {}".format(out_path))
    print("Overall: " + ("PASS" if result["is_pass"] else "FAIL"))

    return output


if __name__ == "__main__":
    main_cli()
