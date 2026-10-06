#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
for argument in "$@"; do
    if [[ "$argument" != '--zip' ]]; then
        echo "Usage: bash scripts/build.sh [--zip]" >&2
        exit 2
    fi
done
echo 'Unload Outfit Studio in Dalamud and stop conversions before updating release/.'
dotnet test tests/OutfitStudio.Tests/OutfitStudio.Tests.csproj -c Release
dotnet build src/OutfitStudio.Plugin/OutfitStudio.Plugin.csproj -c Release
mkdir -p artifacts
worker_stage=$(mktemp -d artifacts/.worker-publish-XXXXXXXX)
trap 'rm -rf -- "$worker_stage"' EXIT
dotnet publish src/OutfitStudio.Worker/OutfitStudio.Worker.csproj -c Release -r win-x64 --self-contained true -o "$worker_stage"
python3 scripts/package.py --worker "$worker_stage" "$@"
