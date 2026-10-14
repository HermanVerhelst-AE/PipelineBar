#!/bin/bash
# Bouwt PipelineBar.exe voor Windows vanaf macOS of Linux.
# Gebruik: ./build.sh          (x64, de meeste pc's)
#          ./build.sh arm64    (Windows op ARM, bv. Snapdragon-laptops)
set -euo pipefail

ARCH="${1:-x64}"
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$(cd "$HERE/.." && pwd)/build/windows-$ARCH"

if ! command -v dotnet >/dev/null 2>&1; then
  echo ".NET SDK niet gevonden. Installeer .NET 10: https://dotnet.microsoft.com/download"
  exit 1
fi

echo "› Publiceren voor win-$ARCH…"
rm -rf "$OUT"
dotnet publish "$HERE/PipelineBar.csproj" -c Release -r "win-$ARCH" --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none \
  -o "$OUT" --nologo -v quiet

echo "✓ $OUT/PipelineBar.exe ($(du -h "$OUT/PipelineBar.exe" | cut -f1))"
