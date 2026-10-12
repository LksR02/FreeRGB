# Recompila o LumeRGB.exe com o compilador C# que já vem no Windows (.NET Framework 4.x).
$ErrorActionPreference = 'Stop'
$appDirectory = Split-Path -Parent $PSScriptRoot
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $compilerPath)) { throw 'Compilador .NET Framework 4 não encontrado.' }
# No pacote as DLLs do WebView2 ficam ao lado do exe; no repositório, na pasta lib.
$libDirectory = Join-Path $appDirectory 'lib'
if (-not (Test-Path -LiteralPath (Join-Path $libDirectory 'Microsoft.Web.WebView2.Core.dll'))) { $libDirectory = $appDirectory }
$sources = 'LumeApp.cs','LumeWindow.cs','LumeService.cs','DesktopWidgets.cs','PersonalSettings.cs','WindowsDevices.cs','MsiRgbController.cs','X68KeyboardController.cs','LightingHub.cs' | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compilerPath /nologo /target:winexe /platform:x64 /optimize+ "/win32icon:$PSScriptRoot\lume.ico" "/out:$appDirectory\LumeRGB.exe" `
  /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
  /reference:System.Runtime.Serialization.dll /reference:System.Web.Extensions.dll `
  "/reference:$libDirectory\Microsoft.Web.WebView2.Core.dll" "/reference:$libDirectory\Microsoft.Web.WebView2.WinForms.dll" $sources
if ($LASTEXITCODE -ne 0) { throw 'A compilação falhou.' }
if ($libDirectory -ne $appDirectory) {
  Copy-Item (Join-Path $libDirectory '*.dll') $appDirectory -Force
}
Write-Output "Compilado: $appDirectory\LumeRGB.exe"
