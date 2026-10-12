# CI on Windows: builds LumeRGB.exe exactly like COMPILAR.bat / the in-app updater (csc of .NET Framework 4,
# source\build.rsp), then compiles and runs the hardware-free tests on the real .NET Framework.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'source'
$bin = Join-Path $root 'dist\win-tests'

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $src 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'build.ps1 falhou.' }
if (-not (Test-Path -LiteralPath (Join-Path $root 'LumeRGB.exe'))) { throw 'LumeRGB.exe não foi gerado.' }

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force -Path $bin | Out-Null
Copy-Item (Join-Path $root 'lib\*.dll') $bin -Force
$core = 'MsiRgbController.cs', 'X68KeyboardController.cs', 'WindowsDevices.cs', 'LightingHub.cs', 'LampArray.cs', 'PersonalSettings.cs',
        'LumeService.cs', 'Sensors.cs', 'Reactive.cs' | ForEach-Object { Join-Path $src $_ }
$refs = '/reference:System.Drawing.dll', '/reference:System.Runtime.Serialization.dll', '/reference:System.Management.dll'

function Invoke-Test([string]$name, [string[]]$extra) {
    $exe = Join-Path $bin "$name.exe"
    # csc applies /out: to the source files that follow it, so it must come before every .cs file.
    & $csc /nologo /langversion:5 /platform:x64 "/out:$exe" $refs $extra $core (Join-Path $src "tests\$name.cs")
    if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar $name." }
    # Windows PowerShell 5.1 turns any stderr line of a native program into a terminating error under 'Stop':
    # run the test with 'Continue' and judge it by its exit code only.
    $ErrorActionPreference = 'Continue'
    $output = & $exe 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $output | Select-Object -Last 1 | Write-Output
    if ($code -ne 0) { $output | Write-Output; throw "$name falhou." }
}

# Same working folder as tools/package.sh (some tests read files such as lume.ico from source\).
Push-Location $src
foreach ($test in 'MsiNativeTests', 'X68KeyboardControllerTests', 'SettingsTests', 'ServiceTests', 'ReactiveTests') { Invoke-Test $test @() }
Invoke-Test 'UpdaterOfflineTests' @('/reference:System.Web.Extensions.dll', '/reference:System.IO.Compression.dll', '/reference:System.IO.Compression.FileSystem.dll', (Join-Path $src 'Updater.cs'))
Invoke-Test 'AdvancedSensorsTests' @('/reference:System.Windows.Forms.dll', "/reference:$(Join-Path $root 'lib\LibreHardwareMonitorLib.dll')", (Join-Path $src 'HardwareMonitor.cs'))
Invoke-Test 'SensorWorkerTests' @('/reference:System.Windows.Forms.dll', "/reference:$(Join-Path $root 'lib\LibreHardwareMonitorLib.dll')", (Join-Path $src 'HardwareMonitor.cs'))
Pop-Location
Write-Output 'Windows: compilação e testes OK.'
