#!/usr/bin/env bash
# Builds LumeRGB.exe with Mono's C# compiler (language level 5, same as the Windows csc),
# runs the hardware-free tests and writes dist/LumeRGB.zip. Usage: tools/package.sh [extra-dir-to-include]
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/dist"; stage="$out/LumeRGB"; bin="$out/bin"
rm -rf "$stage" "$bin" "$out/LumeRGB.zip"; mkdir -p "$stage" "$bin"
cd "$root/source"
core="MsiRgbController.cs X68KeyboardController.cs WindowsDevices.cs LightingHub.cs PersonalSettings.cs LumeService.cs"
refs="-r:System.Drawing.dll -r:System.Runtime.Serialization.dll"
for t in MsiNativeTests X68KeyboardControllerTests SettingsTests ServiceTests; do
  mcs -langversion:5 -nologo $refs -out:"$bin/$t.exe" $core "tests/$t.cs"
  mono "$bin/$t.exe" | tail -1
done
mcs -langversion:5 -nologo $refs -r:System.Windows.Forms.dll -out:"$bin/WidgetPreview.exe" $core DesktopWidgets.cs tests/WidgetPreview.cs
mono "$bin/WidgetPreview.exe" "$bin" >/dev/null
mcs -langversion:5 -nologo -target:winexe -platform:x64 -optimize+ -win32icon:lume.ico -out:"$stage/LumeRGB.exe" \
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.Runtime.Serialization.dll -r:System.Web.Extensions.dll \
  -r:../lib/Microsoft.Web.WebView2.Core.dll -r:../lib/Microsoft.Web.WebView2.WinForms.dll \
  LumeApp.cs LumeWindow.cs LumeService.cs DesktopWidgets.cs PersonalSettings.cs WindowsDevices.cs MsiRgbController.cs X68KeyboardController.cs LightingHub.cs
cp "$root"/lib/*.dll "$stage/"
cp -r "$root/ui" "$stage/ui"
mkdir -p "$stage/source"; cp "$root"/source/*.cs "$root/source/build.ps1" "$root/source/lume.ico" "$stage/source/"; cp -r "$root/source/tests" "$stage/source/tests"
cp "$root/LEIA-ME.txt" "$root/ATRIBUICOES.txt" "$root/COMPILAR.bat" "$stage/"; cp "$root/LICENSE-GPL-2.0.txt" "$stage/LICENSE"; cp "$root/lib/WebView2-LICENSE.txt" "$stage/"
mkdir -p "$stage/previas"; cp "$root"/docs/previews/*.png "$stage/previas/"
if [ "${1:-}" != "" ] && [ -d "$1" ]; then cp -r "$1" "$stage/"; fi
(cd "$out" && zip -qr LumeRGB.zip LumeRGB)
echo "Pacote: $out/LumeRGB.zip ($(du -h "$out/LumeRGB.zip" | cut -f1))"
