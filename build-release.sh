#!/usr/bin/env bash
# Builds a release zip for the Jellyfin plugin repository and prints the
# manifest fields (checksum, size) you need to update in manifest.json.
set -euo pipefail

VERSION=$(grep -oP '(?<="version": ")[^"]+' meta.json)
NAME="strm-audio"
OUT="release"

echo "Building STRM Audio $VERSION ..."
dotnet publish -c Release -o publish

rm -rf "$OUT" && mkdir -p "$OUT/stage"
cp publish/Jellyfin.Plugin.StrmAudio.dll meta.json logo.png "$OUT/stage/"

ZIP="$OUT/${NAME}_${VERSION}.zip"
(cd "$OUT/stage" && zip -q "../${NAME}_${VERSION}.zip" ./*)
rm -rf "$OUT/stage"

MD5=$(md5sum "$ZIP" | cut -d' ' -f1)
TS=$(date -u +%Y-%m-%dT%H:%M:%SZ)

echo
echo "Release zip : $ZIP"
echo "checksum    : $MD5"
echo "timestamp   : $TS"
echo
echo "Manifest version entry:"
cat << JSON
{
    "version": "$VERSION",
    "changelog": "<fill in>",
    "targetAbi": "10.11.0.0",
    "sourceUrl": "https://github.com/blackwhitebear8/Jellyfin-STRM-Audio/releases/download/v$VERSION/${NAME}_${VERSION}.zip",
    "checksum": "$MD5",
    "timestamp": "$TS"
}
JSON
