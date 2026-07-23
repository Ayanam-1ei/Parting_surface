# 分型面尖钢审查 NX 插件 (PartingSurfaceReview)

## 功能

在 NX 内一键检测当前工作部件的分型面尖钢风险：
- 自动识别分型面片体和产品实体
- 几何检测：短边/微边、窄非平面面、面夹角
- 输出审查报告（文本格式）
- 一键创建受保护工作副本并自动修复
- 源文件始终只读不修改

## 文件结构

```
nxplugin/
├── deploy/                              # 部署包
│   ├── application/PartingSurfaceReview.dll
│   ├── startup/PartingSurfaceReview.men
│   ├── install.ps1                      # 一键安装
│   └── uninstall.ps1                    # 卸载
├── Analysis/GeometryAnalyzer.cs         # 几何分析
├── Repair/FaceRepairOps.cs              # 修复操作
├── Reporting/ReportWriter.cs            # 报告输出
├── ReviewTypes.cs                       # 数据类型
├── PartingSurfaceReviewCommand.cs       # 主入口
└── PartingSurfaceReview.csproj          # 项目文件
```

## 安装

1. 以管理员身份打开 PowerShell
2. 进入 deploy 目录
3. 运行: `.\install.ps1`
4. 重启 NX

## 使用

1. 在 NX 中打开要审查的 .prt 部件
2. 菜单栏 -> 分型面审查 -> 审查分型面
3. 查看 NX 信息窗口中的检测结果
4. 根据弹窗选择：
   - 【是】创建受保护工作副本并自动修复
   - 【否】仅输出报告
   - 【取消】结束

## 编译

```powershell
# 使用 MSBuild (需要 Visual Studio)
& "G:\vs\MSBuild\Current\Bin\MSBuild.exe" PartingSurfaceReview.csproj /p:Configuration=Release

# 或使用 Roslyn csc 直接编译
$roslyn = "G:\vs\MSBuild\Current\Bin\Roslyn\csc.exe"
& $roslyn /target:library /platform:x64 /optimize+ `
  /r:S:\nx\NXBIN\managed\NXOpen.dll `
  /r:S:\nx\NXBIN\managed\NXOpen.UF.dll `
  /r:S:\nx\NXBIN\managed\NXOpen.Utilities.dll `
  /r:S:\nx\NXBIN\managed\NXOpenUI.dll `
  /r:"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\mscorlib.dll" `
  /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll `
  /out:bin\Release\PartingSurfaceReview.dll `
  ReviewTypes.cs Analysis\GeometryAnalyzer.cs Repair\FaceRepairOps.cs Reporting\ReportWriter.cs PartingSurfaceReviewCommand.cs
```

## 故障排查

如果菜单未出现：
1. 检查 NX syslog: `%TEMP%\nx*log*` 搜索 "PartingSurface"
2. 手动加载: 文件 -> 执行 -> NX Open -> 选择 PartingSurfaceReview.dll
3. 确认文件在 `%UGII_BASE_DIR%\startup\` 下

## 规则配置

审查阈值定义在 `skills/parting-surface-review/rules/review_rules.json`