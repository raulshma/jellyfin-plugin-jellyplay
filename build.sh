#!/usr/bin/env bash
# Builds the plugin and produces dist/Jellyfin.Plugin.JellyPlay.zip
# Usage: ./build.sh [version]   (version defaults to csproj Version)
set -euo pipefail
cd "$(dirname "$0")"

VERSION="${1:-}"
if [ -z "$VERSION" ]; then
  VERSION=$(grep -oPm1 '(?<=<Version>)[^<]+' src/Jellyfin.Plugin.JellyPlay/Jellyfin.Plugin.JellyPlay.csproj)
fi

STAMPED="src/Jellyfin.Plugin.JellyPlay/Jellyfin.Plugin.JellyPlay.csproj"
sed -i.bak "s|<Version>.*</Version>|<Version>${VERSION}</Version>|; s|<AssemblyVersion>.*</AssemblyVersion>|<AssemblyVersion>${VERSION}.0</AssemblyVersion>|; s|<FileVersion>.*</FileVersion>|<FileVersion>${VERSION}.0</FileVersion>|" "$STAMPED"

dotnet publish "$STAMPED" -c Release -o dist/publish

mkdir -p dist
STAGE=dist/stage
rm -rf "$STAGE"
mkdir -p "$STAGE"

# Host-provided assemblies must NOT ship in the zip.
EXCLUDE='(Jellyfin.Controller|Jellyfin.Model|MediaBrowser\.(Common|Controller|Model)|Jellyfin\.Data|Jellyfin\.Database|Microsoft\.Data\.Sqlite|SQLitePCLRaw|Newtonsoft\.Json|System\..*|Microsoft\..*|netstandard|StdSdk|YamlDotNet)'
while IFS= read -r file; do
  base=$(basename "$file")
  if echo "$base" | grep -qvE "$EXCLUDE"; then
    cp "$file" "$STAGE/"
  fi
done < <(find dist/publish -maxdepth 1 -type f \( -name '*.dll' -o -name '*.pdb' \) ! -name 'Jellyfin.Plugin.JellyPlay.dll' )
cp dist/publish/Jellyfin.Plugin.JellyPlay.dll "$STAGE/"

# Keep YamlDotNet (not host-provided) — copy it back explicitly if excluded above.
cp dist/publish/YamlDotNet*.dll "$STAGE/" 2>/dev/null || true
cp dist/publish/Microsoft.Data.Sqlite*.dll "$STAGE/" 2>/dev/null || true
cp dist/publish/SQLitePCLRaw.*.dll "$STAGE/" 2>/dev/null || true
# MailKit/MimeKit (+ its BouncyCastle dependency) are not host-provided either;
# re-added defensively in case the exclude pattern above ever broadens.
cp dist/publish/MailKit*.dll "$STAGE/" 2>/dev/null || true
cp dist/publish/MimeKit*.dll "$STAGE/" 2>/dev/null || true
cp dist/publish/BouncyCastle*.dll "$STAGE/" 2>/dev/null || true

rm -f "dist/Jellyfin.Plugin.JellyPlay.zip"
(cd "$STAGE" && zip -q -r ../Jellyfin.Plugin.JellyPlay.zip .)
rm -rf "$STAGE" dist/publish

echo "Built dist/Jellyfin.Plugin.JellyPlay.zip (version $VERSION):"
unzip -l "dist/Jellyfin.Plugin.JellyPlay.zip"
