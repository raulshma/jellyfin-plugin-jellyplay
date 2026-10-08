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
rm -f "${STAMPED}.bak"

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

# Dashboard icon: Jellyfin 12 serves /Plugins/{guid}/{version}/Image from the
# meta.json "imagePath" file, which must sit in the plugin folder next to
# meta.json — no auto-discovery, so both the png and the manifest ship in the zip.
# (Embedded-image resources only work for plugins compiled into the server.)
cp images/plugin.png "$STAGE/plugin.png"
TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
cat > "$STAGE/meta.json" <<EOF
{
  "category": "General",
  "changelog": "",
  "description": "Companion plugin for the JellyPlay client: settings/profile sync, admin defaults, events and messages, Seerr SSO bridge and proxy, newsletter backend, ratings aggregation, custom/seasonal home rows, anime markers, recommendations, book bookmarks and transcode insights.",
  "guid": "d3f1a6c8-5b2e-4d7f-9a0c-6e8b1f4d2a7c",
  "name": "JellyPlay",
  "overview": "Companion plugin for the JellyPlay client",
  "owner": "raulshma",
  "targetAbi": "",
  "timestamp": "$TIMESTAMP",
  "version": "${VERSION}.0",
  "status": "Active",
  "autoUpdate": false,
  "imagePath": "plugin.png",
  "assemblies": []
}
EOF

rm -f "dist/Jellyfin.Plugin.JellyPlay.zip"
zip_stage() {
  # Prefer Info-ZIP; fall back to Windows' bsdtar (-a infers zip) or PowerShell.
  if command -v zip >/dev/null 2>&1; then
    (cd "$STAGE" && zip -q -r "../Jellyfin.Plugin.JellyPlay.zip" .)
  elif [ -x "/c/Windows/System32/tar.exe" ]; then
    (cd "$STAGE" && /c/Windows/System32/tar.exe -a -cf "../Jellyfin.Plugin.JellyPlay.zip" .)
  else
    (cd "$STAGE" && pwsh -NoProfile -Command "Compress-Archive -Path (Get-Location).Path + '/*' -DestinationPath '../Jellyfin.Plugin.JellyPlay.zip' -Force")
  fi
}
zip_stage
rm -rf "$STAGE" dist/publish

echo "Built dist/Jellyfin.Plugin.JellyPlay.zip (version $VERSION):"
unzip -l "dist/Jellyfin.Plugin.JellyPlay.zip"
