#!/usr/bin/env bash
# Deploys the plugin to a local Jellyfin plugins dir for development.
# Usage: ./local.sh /path/to/jellyfin/plugins [/path/to/jellyfin-server]
set -euo pipefail
cd "$(dirname "$0")"

PLUGINS_DIR="${1:?usage: local.sh <plugins-dir>}"
./build.sh

mkdir -p "$PLUGINS_DIR"
unzip -o "dist/Jellyfin.Plugin.JellyPlay.zip" -d "$PLUGINS_DIR" >/dev/null
echo "Deployed to $PLUGINS_DIR — restart Jellyfin to load."
