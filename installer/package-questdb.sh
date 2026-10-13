#!/usr/bin/env bash
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
DEST="${1:?Usage: package-questdb.sh published-runtime-directory}"
MANIFEST="$HERE/questdb-release.json"
VER="$(node -e 'process.stdout.write(require(process.argv[1]).version)' "$MANIFEST")"
ASSET="$(node -e 'process.stdout.write(require(process.argv[1]).linux.asset)' "$MANIFEST")"
HASH="$(node -e 'process.stdout.write(require(process.argv[1]).linux.sha256)' "$MANIFEST")"
CACHE="${IGW_QUESTDB_CACHE:-$HOME/.cache/igw/questdb}/$VER"
mkdir -p "$CACHE"
if [[ ! -f "$CACHE/$ASSET" ]]; then
  curl --fail --location --output "$CACHE/$ASSET" "https://github.com/questdb/questdb/releases/download/$VER/$ASSET"
fi
printf '%s  %s\n' "$HASH" "$CACHE/$ASSET" | sha256sum --check --status
[[ -d "$DEST" && ! -e "$DEST/questdb" ]] || { echo 'Use a fresh IGW publish directory.' >&2; exit 1; }
mkdir "$DEST/questdb"
tar -xzf "$CACHE/$ASSET" --strip-components=1 -C "$DEST/questdb"
test -f "$DEST/questdb/legal/LICENSE.txt"
cp "$MANIFEST" "$DEST/questdb/igw-release.json"
chmod +x "$DEST/questdb/bin/java"
echo "Staged verified QuestDB $VER Linux runtime."
