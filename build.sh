#!/usr/bin/env bash
# Builds the release zips into dist/:  ./build.sh [version]
set -euo pipefail
cd "$(dirname "$0")"
VERSION=${1:-1.0.0}
rm -rf dist && mkdir -p dist
for rid in win-x64 linux-x64; do
  dotnet publish src/QuiVrRevival/QuiVrRevival.csproj -c Release -r "$rid" -p:Version="$VERSION" -o "dist/$rid"
done

stage=dist/stage-windows && mkdir -p $stage
cp dist/win-x64/QuiVrRevival.exe packaging/README.txt $stage/
(cd $stage && zip -q -9 "../QuiVrRevival-$VERSION-windows.zip" *)

# Linux: native tool + the Windows exe, which the game starts itself under Proton.
stage=dist/stage-linux && mkdir -p $stage
cp dist/linux-x64/QuiVrRevival dist/win-x64/QuiVrRevival.exe packaging/README.txt $stage/
(cd $stage && zip -q -9 "../QuiVrRevival-$VERSION-linux.zip" *)

rm -rf dist/stage-*
ls -la dist/*.zip
