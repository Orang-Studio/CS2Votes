#!/usr/bin/env bash
# Builds the plugin and makes the release zips in dist/
set -euo pipefail
cd "$(dirname "$0")/.."

version=$(scripts/version.sh "$@")
out=src/bin/Release/net10.0
stage=dist/stage/addons/counterstrikesharp/plugins/CS2Votes
dotnet build src/CS2Votes.csproj -c Release -p:ContinuousIntegrationBuild=true
rm -rf dist
mkdir -p "$stage/lang"
cp "$out/CS2Votes.dll" "$out/CS2Votes.pdb" "$stage/"
cp "$out"/lang/*.json "$stage/lang/"
(cd dist/stage && zip -qr "../CS2Votes-$version.zip" addons)
git archive --format=zip --prefix="CS2Votes-$version/" -o "dist/CS2Votes-$version-source.zip" HEAD
rm -rf dist/stage
(cd dist && sha256sum ./*.zip > SHA256SUMS.txt)
ls -l dist