#!/usr/bin/env bash
# Builds LumeRGB.exe with Mono's C# compiler (language level 5, same as the Windows csc),
# runs the hardware-free tests and writes dist/LumeRGB.zip. Usage: tools/package.sh [extra-dir-to-include]
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/dist"; stage="$out/LumeRGB"; bin="$out/bin"
rm -rf "$stage" "$bin" "$out/LumeRGB.zip"; mkdir -p "$stage" "$bin"
cd "$root/source"
core="MsiRgbController.cs X68KeyboardController.cs WindowsDevices.cs LightingHub.cs PersonalSettings.cs LumeService.cs Sensors.cs Reactive.cs"
refs="-r:System.Drawing.dll -r:System.Runtime.Serialization.dll -r:System.Management.dll"
for t in MsiNativeTests X68KeyboardControllerTests SettingsTests ServiceTests ReactiveTests; do
  mcs -langversion:5 -nologo $refs -out:"$bin/$t.exe" $core "tests/$t.cs"
  mono "$bin/$t.exe" | tail -1
done
mcs -langversion:5 -nologo $refs -r:System.Windows.Forms.dll -out:"$bin/WidgetPreview.exe" $core DesktopWidgets.cs tests/WidgetPreview.cs
mono "$bin/WidgetPreview.exe" "$bin" >/dev/null
# Same options file as COMPILAR.bat and the in-app updater.
mcs -langversion:5 @build.rsp -out:"$stage/LumeRGB.exe" -win32icon:lume.ico -lib:../lib *.cs
cp "$root"/lib/*.dll "$stage/"
cp -r "$root/ui" "$stage/ui"
mkdir -p "$stage/source"; cp "$root"/source/*.cs "$root/source/build.ps1" "$root/source/build.rsp" "$root/source/lume.ico" "$stage/source/"; cp -r "$root/source/tests" "$stage/source/tests"
cp "$root/LEIA-ME.txt" "$root/ATRIBUICOES.txt" "$root/COMPILAR.bat" "$stage/"; cp "$root/LICENSE-GPL-2.0.txt" "$stage/LICENSE"; cp "$root/lib/WebView2-LICENSE.txt" "$root/lib/SENSORES-LICENCAS.txt" "$stage/"
mkdir -p "$stage/previas"; cp "$root"/docs/previews/*.png "$stage/previas/"
if [ "${1:-}" != "" ] && [ -d "$1" ]; then cp -r "$1" "$stage/"; fi
# The updater compares this commit with GitHub main.
printf '{"commit":"%s","date":"%s","branch":"main"}' "$(git -C "$root" rev-parse HEAD)" "$(git -C "$root" log -1 --format=%cI)" > "$stage/version.json"
(cd "$out" && zip -qr LumeRGB.zip LumeRGB)
echo "Pacote: $out/LumeRGB.zip ($(du -h "$out/LumeRGB.zip" | cut -f1))"
