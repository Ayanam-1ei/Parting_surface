@echo off
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set NX=S:\nx\NXBIN\managed
set OUT=nxplugin\bin\Release\PartingSurfaceReview.dll

%CSC% /target:library /platform:x64 /optimize+ /nologo ^
  /reference:"%NX%\NXOpen.dll" ^
  /reference:"%NX%\NXOpen.UF.dll" ^
  /reference:"%NX%\NXOpen.Utilities.dll" ^
  /reference:"%NX%\NXOpenUI.dll" ^
  /reference:"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\mscorlib.dll" ^
  /reference:System.dll ^
  /reference:System.Core.dll ^
  /reference:System.Windows.Forms.dll ^
  /out:"%OUT%" ^
  nxplugin\ReviewTypes.cs ^
  nxplugin\Analysis\GeometryAnalyzer.cs ^
  nxplugin\Repair\FaceRepairOps.cs ^
  nxplugin\Reporting\ReportWriter.cs ^
  nxplugin\PartingSurfaceReviewCommand.cs

if %ERRORLEVEL% equ 0 (
  echo BUILD SUCCESS
  dir "%OUT%"
) else (
  echo BUILD FAILED
)
