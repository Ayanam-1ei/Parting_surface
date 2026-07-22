# Parting Surface Review

这是一个本地分型面尖钢 AI 审查工作流：

- 确定性 Parasolid 算法解析 `.prt` 并量测 B-rep。
- 规则引擎输出可信证据、缺失项和风险等级。
- Codex Skill 基于证据做工程判断和修改建议。
- Session 自动对比修改副本，形成最多 5 轮的关闭 Loop。
- SHA-256 门禁保证工作流不修改原始 `.prt`。

## 运行边界

“不打开 NX”指不启动 NX GUI。机器仍需安装合法 Siemens NX/Parasolid 运行时；项目使用 `ug_inspect` 提取 Parasolid 分区，再通过 `pskernel_net.dll` 读取 B-rep。项目不提供服务器模式，也不绕过 Siemens 许可证。

当前版本可生成安全工作副本和修改计划，但不伪装成自动改模器。只有合法 NX Headless 修改器真正改变工作副本、且修改版复审无 ERROR 后，才允许输出 `_reviewed_*.prt`。源文件始终保持不变。

## 快速开始

```powershell
python "skills\parting-surface-review\scripts\review_workflow.py" `
  "D:\data\part.prt" `
  --session "D:\review\part-session" `
  --nx-root "S:\nx"
```

首轮输出：

```text
part-session/
├── session.json
└── round-01/
    ├── geometry_evidence.json
    ├── review_result.json
    └── review_report.md
```

修改后把新文件作为同一 Session 的下一轮输入：

```powershell
python "skills\parting-surface-review\scripts\review_workflow.py" `
  "D:\data\part_working_r01.prt" `
  --session "D:\review\part-session"
```

## 判定原则

确认几何风险必须同时满足：

1. 相邻面距离产品不大于 0.1 mm。
2. 存在最小包围盒尺寸不大于 1.0 mm 的非平面相邻面。
3. 边长不大于 0.1 mm 且面夹角为 1°～45°。

候选使用较宽门槛：边长不大于 1.5 mm、面夹角为 0.5°～15°，其余条件相同。曲线半径不能单独触发候选；边长不能代替钢厚。详细阈值见 `skills/parting-surface-review/rules/review_rules.json`。

## 目录

- `analyzer/ParasolidAnalyzer.cs`：Parasolid B-rep 确定性量测核心。
- `skills/parting-surface-review/scripts/geometry_pipeline.py`：分区提取与证据组装。
- `skills/parting-surface-review/scripts/review_engine.py`：规则、报告与问题匹配。
- `skills/parting-surface-review/scripts/review_workflow.py`：Session 与端到端 CLI。
- `skills/parting-surface-review/scripts/repair_copy.py`：源文件保护和交付门禁。

## 已知限制

- 没有独立型腔/型芯钢料实体时，真实钢厚、有效高度和细长比为 `UNAVAILABLE`。
- 没有开模方向时，倒扣与最大轮廓分型位置为 `UNAVAILABLE`。
- 几何候选是审图入口，不替代模具总工对承压、材料、寿命、浇口和加工能力的判断。
