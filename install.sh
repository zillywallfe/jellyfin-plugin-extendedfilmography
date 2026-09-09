#!/usr/bin/env bash
# ThinkPad bind-mount installer. Build/test first, then back up and replace this plugin only.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
CONTAINER="${JELLYFIN_CONTAINER:-jellyfin}"
SDK_IMAGE="${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:9.0}"
PLUGINS="${JELLYFIN_PLUGINS_HOST:-$HOME/plex_config/jellyfin/data/plugins}"
VERSION=1.0.1.0
DLL=Jellyfin.Plugin.ExtendedFilmography.dll
BUILD_ONLY=0
case "${1:-}" in --build-only) BUILD_ONLY=1;; '') ;; *) echo 'Usage: ./install.sh [--build-only]' >&2; exit 1;; esac
case "$ROOT/" in */plugins/*) echo 'Move this source directory outside all plugins folders before building.' >&2; exit 1;; esac
command -v docker >/dev/null
mkdir -p "$ROOT/artifacts/publish" "$ROOT/.nuget-cache"
echo 'Building and running regression checks; Jellyfin is still running.'
docker run --rm -u "$(id -u):$(id -g)" \
  -e DOTNET_CLI_HOME=/tmp -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  -e NUGET_PACKAGES=/nuget -v "$ROOT:/src" -v "$ROOT/.nuget-cache:/nuget" \
  -w /src "$SDK_IMAGE" sh -ec '
    dotnet run --project verify/Verify.csproj -c Release
    dotnet publish src/Jellyfin.Plugin.ExtendedFilmography/Jellyfin.Plugin.ExtendedFilmography.csproj -c Release -o /src/artifacts/publish
  '
test -s "$ROOT/artifacts/publish/$DLL"
if [[ "$BUILD_ONLY" == 1 ]]; then echo "Built $ROOT/artifacts/publish/$DLL"; exit 0; fi
test -d "$PLUGINS" || { echo "Missing host plugin folder: $PLUGINS" >&2; exit 1; }
# Match the working installation, never the root user returned by docker exec id.
REFERENCE="$PLUGINS/AniDB_11.0.0.0"
test -d "$REFERENCE" || REFERENCE="$PLUGINS"
OWNER="$(stat -c '%u:%g' "$REFERENCE")"
BACKUP="${JELLYFIN_BACKUP_HOST:-$HOME/jellyfin-plugin-backups}/$(date +%Y%m%d-%H%M%S)-$$"
mkdir -p "$BACKUP/active" "$BACKUP/legacy"
sudo -v
docker inspect "$CONTAINER" >/dev/null
# Build failures above do not interrupt the server. Failures below leave backups intact.
docker stop "$CONTAINER"
trap 'echo "Installation interrupted. Backups: $BACKUP. Correct the error before starting Jellyfin." >&2' ERR
shopt -s nullglob
for old in "$PLUGINS"/Extended\ Filmography_*; do
  sudo mv "$old" "$BACKUP/active/"
done
# Remove the misplaced DLL installation made by the original installer.
if [[ "$PLUGINS" == */data/plugins ]]; then
  LEGACY="${PLUGINS%/data/plugins}/plugins"
  for old in "$LEGACY"/Extended\ Filmography_*; do
    sudo mv "$old" "$BACKUP/legacy/"
  done
fi
DEST="$PLUGINS/Extended Filmography_$VERSION"
sudo mkdir -p "$DEST"
sudo cp "$ROOT/artifacts/publish/$DLL" "$DEST/$DLL"
sudo cp "$ROOT/meta.json" "$DEST/meta.json"
sudo chown -R "$OWNER" "$DEST"
sudo chmod 755 "$DEST"
sudo chmod 644 "$DEST/$DLL" "$DEST/meta.json"
# Install a fresh Active manifest, without carrying forward stale Disabled/Malfunctioned state.
# Jellyfin can update the manifest because its user owns this directory.
docker start "$CONTAINER"
trap - ERR
printf '\nInstalled %s. Previous installations backed up at %s\n' "$VERSION" "$BACKUP"
echo 'Saved API keys and plugin settings are retained. Check Dashboard -> Plugins.'
echo 'For comparisons, uncheck Enable extended filmography inside its settings and Save.'
