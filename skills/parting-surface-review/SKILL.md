---
name: parting-surface-review
description: Review Siemens NX .prt files for parting-surface sharp-steel risks without opening the NX GUI. Use when a user sends or references a .prt and asks to inspect 分型面、尖钢、薄弱钢料, generate deterministic geometry evidence, compare a modified copy in an automatic review Loop, or prepare a protected working copy while leaving the source unchanged.
---

# 分型面尖钢审查

对 `.prt` 执行本地、无 NX GUI 的审查。确定性 Parasolid 算法负责量测与候选筛查；你负责工程判断、风险解释和修改建议。

## 前提

- 要求本机有合法 Siemens NX/Parasolid 运行时；默认自动查找，或用 `--nx-root` 指定。
- 要求 Windows C# 编译器；默认查找 .NET Framework 或 Visual Studio。
- 不使用服务器，不上传 `.prt`，不启动 NX GUI，不规避许可证。
- 永远不修改用户源文件。修改任务只允许操作工作副本。

## 首轮审查

1. 确认用户给出的 `.prt` 路径可读。
2. 在可写目录建立独立 Session。
3. 运行：

```powershell
python "<skill-dir>\scripts\review_workflow.py" "<input.prt>" --session "<session-dir>"
```

4. 读取该轮的 `geometry_evidence.json`、`review_result.json` 和 `review_report.md`。
5. 向用户报告：
   - 确认几何风险和候选数量。
   - 每个问题的坐标、边长、面夹角、窄面尺寸和产品距离。
   - 数据不可用项及原因。
   - 方案 A、方案 B 和可复核标准。

## 工程判断约束

- `confirmed_geometry_risk` 是确定性几何命中，不等同于已量得真实钢厚。
- `candidate` 必须表述为待模具工程确认，不得升级成确定错误。
- 边长不是钢厚；曲线最小半径不是真实钢料圆角。
- 缺失钢厚、高度、细长比、开模方向或倒扣证据时，保留 `null/UNAVAILABLE`。
- 不得凭经验补造移动距离、镶件尺寸、寿命、材料、R 值或通过结论。
- AI 建议必须引用证据坐标，并给出复跑后可检查的验收条件。

## 自动 Loop

用户给出修改副本后，复用同一 Session：

```powershell
python "<skill-dir>\scripts\review_workflow.py" "<modified-copy.prt>" --session "<session-dir>"
```

工作流按稳定指纹和 3 mm 邻域自动匹配上一轮问题，输出新增、改善、遗留、关闭和退化项。最多 5 轮；达到上限仍有风险时转人工评审，绝不强制判定通过。

## 工作副本

需要保护性副本时运行：

```powershell
python "<skill-dir>\scripts\review_workflow.py" "<input.prt>" --session "<session-dir>" --prepare-working-copy
```

这只生成哈希一致的 `_working_*.prt` 和清单，不代表已修改。只有合法 NX Headless 修改成功、修改副本哈希已变化且复审无 ERROR 后，才可用：

```powershell
python "<skill-dir>\scripts\repair_copy.py" finalize "<working.manifest.json>" "<review_result.json>"
```

交付名使用 `_reviewed_*.prt`，不得声称 `fixed` 或覆盖源文件。

## 失败处理

- 运行时或编译器缺失：说明依赖，停止几何结论。
- `.prt` 无 Parasolid 分区：报告技术失败，不回退到猜测。
- 无独立分型面 Sheet Body：报告 `UNAVAILABLE`。
- 无型腔/型芯钢料实体：可筛查局部尖锐拓扑，但钢厚、有效高度和细长比不可用。
- 无开模方向：倒扣和最大轮廓位置不可用。

规则阈值位于 `rules/review_rules.json`。不要改用旧 NX Journal 或伪造 Bounding Box 数据。
