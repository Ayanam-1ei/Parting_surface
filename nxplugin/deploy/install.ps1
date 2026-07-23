# ============================================================
#  分型面尖钢审查插件 — 一键安装脚本
#  PartingSurfaceReview NX Plugin Installer
# ============================================================
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " 分型面尖钢审查 插件安装" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# Try to find NX installation
$nxBase = $null
$candidates = @(
    "S:\nx\UGII",
    "C:\Program Files\Siemens\NX2306\UGII",
    "C:\Program Files\Siemens\NX\UGII",
    $env:UGII_BASE_DIR
)

foreach ($c in $candidates) {
    if ($c -and (Test-Path "$c\startup")) {
        $nxBase = $c
        break
    }
}

if (-not $nxBase) {
    Write-Host "未自动检测到 NX，请手动输入 UGII 路径..."
    $nxBase = Read-Host "UGII_BASE_DIR (如 S:\nx\UGII)"
    if (-not (Test-Path "$nxBase\startup")) {
        Write-Host "错误: $nxBase\startup 不存在!" -ForegroundColor Red
        exit 1
    }
}

Write-Host "NX 路径: $nxBase" -ForegroundColor Green

$startupDir = "$nxBase\startup"
$appDir     = "$nxBase\application"

$scriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path

$dllSrc  = Join-Path $scriptDir "application\PartingSurfaceReview.dll"
$menSrc  = Join-Path $scriptDir "startup\PartingSurfaceReview.men"

if (-not (Test-Path $dllSrc)) { Write-Host "错误: 找不到 $dllSrc" -ForegroundColor Red; exit 1 }
if (-not (Test-Path $menSrc)) { Write-Host "错误: 找不到 $menSrc" -ForegroundColor Red; exit 1 }

Write-Host "正在安装..." -ForegroundColor Yellow

# Install DLL to startup (NX scans startup for DLLs in .men ACTIONS)
try {
    Copy-Item $dllSrc $startupDir -Force
    Write-Host "  + DLL -> $startupDir\PartingSurfaceReview.dll" -ForegroundColor Green
} catch {
    Write-Host "  X DLL 复制失败: $_" -ForegroundColor Red
    exit 1
}

# Install .men to startup
try {
    Copy-Item $menSrc $startupDir -Force
    Write-Host "  + MEN -> $startupDir\PartingSurfaceReview.men" -ForegroundColor Green
} catch {
    Write-Host "  X MEN 复制失败: $_" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host " 安装完成!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "使用方法:" -ForegroundColor Yellow
Write-Host "  1. 重启 NX" -ForegroundColor White
Write-Host "  2. 打开部件文件" -ForegroundColor White
Write-Host "  3. 菜单栏 -> 帮助 左侧 -> 分型面审查 -> 审查分型面" -ForegroundColor White
Write-Host ""
Write-Host "如菜单未出现，请在 NX 中检查:" -ForegroundColor DarkYellow
Write-Host "  菜单 -> 工具 -> 日志文件 -> 搜索 'PartingSurface'" -ForegroundColor DarkYellow
Write-Host "  或手动加载: 文件 -> 执行 -> NX Open -> 选择 PartingSurfaceReview.dll" -ForegroundColor DarkYellow