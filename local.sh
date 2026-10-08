#!/usr/bin/env bash
# Deploys the plugin to a local Jellyfin plugins dir for development.
# Usage: ./local.sh /path/to/jellyfin/plugins
set -euo pipefail
cd "$(dirname "$0")"

PLUGINS_DIR="${1:?usage: local.sh <plugins-dir>}"
./build.sh

# Jellyfin 12 layout: plugins/<PluginName>/ holds meta.json, the dashboard icon
# and the dlls. The folder is replaced wholesale so stale dlls from an older
# build (or a migrated version subfolder) can't linger beside the new ones —
# with an empty assemblies whitelist every dll under the folder gets loaded.
TARGET="$PLUGINS_DIR/Jellyfin.Plugin.JellyPlay"
rm -rf "$TARGET"
mkdir -p "$PLUGINS_DIR"
unzip -o "dist/Jellyfin.Plugin.JellyPlay.zip" -d "$TARGET" >/dev/null
echo "Deployed to $TARGET — restart Jellyfin to load."
