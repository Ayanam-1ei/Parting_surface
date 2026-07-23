# ============================================================
#  分型面尖钢审查插件 — 卸载脚本
# ============================================================
Write-Host "分型面尖钢审查 插件卸载" -ForegroundColor Cyan

$nxBase = $null
$candidates = @(
    "S:\nx\UGII",
    "C:\Program Files\Siemens\NX2306\UGII",
    $env:UGII_BASE_DIR
)

foreach ($c in $candidates) {
    if ($c -and (Test-Path "$c\startup")) { $nxBase = $c; break }
}

if (-not $nxBase) {
    $nxBase = Read-Host "UGII_BASE_DIR"
    if (-not (Test-Path "$nxBase\startup")) {
        Write-Host "错误: $nxBase\startup 不存在!" -ForegroundColor Red; exit 1
    }
}

$startupDir = "$nxBase\startup"

$files = @(
    "$startupDir\PartingSurfaceReview.dll",
    "$startupDir\PartingSurfaceReview.men"
)

foreach ($f in $files) {
    if (Test-Path $f) {
        Remove-Item $f -Force
        Write-Host "  已删除: $f" -ForegroundColor Green
    } else {
        Write-Host "  未找到: $f" -ForegroundColor DarkGray
    }
}

Write-Host "卸载完成。请重启 NX。" -ForegroundColor Green