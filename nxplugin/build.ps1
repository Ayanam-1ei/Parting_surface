$ErrorActionPreference = "Stop"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$nx = "S:\nx\NXBIN\managed"

$outDir = "S:\visiable\coaicad\_Source\Parting_surface\nxplugin\bin\Release"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$out = "$outDir\PartingSurfaceReview.dll"

$srcDir = "S:\visiable\coaicad\_Source\Parting_surface\nxplugin"

& $csc /target:library /platform:x64 /optimize+ /nologo `
  /reference:"$nx\NXOpen.dll" `
  /reference:"$nx\NXOpen.UF.dll" `
  /reference:"$nx\NXOpen.Utilities.dll" `
  /reference:"$nx\NXOpenUI.dll" `
  /reference:System.dll `
  /reference:System.Core.dll `
  /reference:System.Windows.Forms.dll `
  /out:"$out" `
  "$srcDir\ReviewTypes.cs" `
  "$srcDir\Analysis\GeometryAnalyzer.cs" `
  "$srcDir\Repair\FaceRepairOps.cs" `
  "$srcDir\Reporting\ReportWriter.cs" `
  "$srcDir\PartingSurfaceReviewCommand.cs"

if ($LASTEXITCODE -eq 0) {
    Write-Host "BUILD SUCCESS"
    Get-Item $out
} else {
    Write-Host "BUILD FAILED with exit code $LASTEXITCODE"
}
