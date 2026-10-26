#!/bin/sh
# Official signed WinDivert 2.2.2 (x64) into dist/.
set -e
cd "$(dirname "$0")"
tmp=$(mktemp -d)
curl -sSL -o "$tmp/wd.zip" https://github.com/basil00/WinDivert/releases/download/v2.2.2/WinDivert-2.2.2-A.zip
unzip -q "$tmp/wd.zip" -d "$tmp"
mkdir -p dist licenses
cp "$tmp"/WinDivert-2.2.2-A/x64/WinDivert.dll "$tmp"/WinDivert-2.2.2-A/x64/WinDivert64.sys dist/
cp "$tmp"/WinDivert-2.2.2-A/LICENSE licenses/WinDivert-LICENSE.txt
rm -rf "$tmp"
