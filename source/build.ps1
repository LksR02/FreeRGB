# Recompila o LumeRGB.exe com o compilador C# que já vem no Windows (.NET Framework 4.x).
# Uso: clique duas vezes em COMPILAR.bat  (ou: powershell -ExecutionPolicy Bypass -File source\build.ps1)
$ErrorActionPreference = 'Stop'
$appDirectory = Split-Path -Parent $PSScriptRoot
Write-Output "Pasta do app: $appDirectory"

# Arquivos vindos de um zip baixado ficam marcados como "da internet"; o .NET e o Windows podem recusá-los.
Get-ChildItem -LiteralPath $appDirectory -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $compilerPath)) { throw 'Compilador do .NET Framework 4 não encontrado (csc.exe).' }
Write-Output "Compilador: $compilerPath"

# No pacote as DLLs do WebView2 ficam ao lado do exe; no repositório, na pasta lib.
$libDirectory = Join-Path $appDirectory 'lib'
if (-not (Test-Path -LiteralPath (Join-Path $libDirectory 'Microsoft.Web.WebView2.Core.dll'))) { $libDirectory = $appDirectory }
foreach ($dll in 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll') {
  if (-not (Test-Path -LiteralPath (Join-Path $libDirectory $dll))) { throw "Arquivo ausente: $dll. Extraia o zip completo antes de compilar." }
}

$sources = Get-ChildItem -LiteralPath $PSScriptRoot -Filter *.cs | Sort-Object Name | ForEach-Object { $_.FullName }
$output = Join-Path $appDirectory 'LumeRGB.exe'
if (Get-Process -Name LumeRGB -ErrorAction SilentlyContinue) { throw 'Feche o Lume RGB (inclusive pelo ícone da bandeja → Sair) antes de compilar.' }

# Mesmas opções do atualizador automático (source\build.rsp).
& $compilerPath "@$PSScriptRoot\build.rsp" "/out:$output" "/win32icon:$PSScriptRoot\lume.ico" "/lib:$libDirectory" $sources
if ($LASTEXITCODE -ne 0) { throw 'A compilação falhou. Copie as mensagens acima e envie para análise.' }
if ($libDirectory -ne $appDirectory) { Copy-Item (Join-Path $libDirectory '*.dll') $appDirectory -Force }
Write-Output ""
Write-Output "Compilado com sucesso: $output"
