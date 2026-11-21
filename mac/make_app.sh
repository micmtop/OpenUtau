#!/bin/bash
set -e
PUB="$1"; APP="$2"
rm -rf "$APP"; mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUB"/* "$APP/Contents/MacOS/"
cp "$(dirname "$0")/Info.plist" "$APP/Contents/Info.plist"
chmod +x "$APP/Contents/MacOS/OpenUtau"
