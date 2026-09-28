#!/usr/bin/env bash
# Build, test and package the C# parts (works on Linux/macOS/Windows with the .NET 10 SDK).
#   dist/PowerCad.bundle/            AutoCAD 2027 autoloader bundle (plugin + manifest)
#   dist/server/<rid>/power-cad-server(.exe)   self-contained MCP server
set -euo pipefail
cd "$(dirname "$0")/.."
CONFIG=${CONFIG:-Release}
RID=${RID:-win-x64}

dotnet restore dotnet/PowerCad.sln
dotnet build dotnet/PowerCad.sln -c "$CONFIG" --no-restore
dotnet test dotnet/PowerCad.Tests -c "$CONFIG" --no-build

rm -rf dist/PowerCad.bundle "dist/server/$RID"
mkdir -p dist/PowerCad.bundle/Contents
cp dotnet/bundle/PowerCad.bundle/PackageContents.xml dist/PowerCad.bundle/
cp dotnet/PowerCad.Plugin.A27/bin/"$CONFIG"/net10.0-windows/PowerCad.*.dll dist/PowerCad.bundle/Contents/
cp dotnet/PowerCad.Plugin.A27/bin/"$CONFIG"/net10.0-windows/PowerCad.Plugin.A27.deps.json dist/PowerCad.bundle/Contents/

dotnet publish dotnet/PowerCad.Server -c "$CONFIG" -r "$RID" --self-contained true \
  -p:PublishSingleFile=true -p:PackAsTool=false -o "dist/server/$RID"
echo "Built dist/PowerCad.bundle and dist/server/$RID"
