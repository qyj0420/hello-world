#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export PATH="${HOME}/.dotnet:${PATH}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
CFG="${1:-Debug}"
PROJECT="$ROOT/factory-press/Xiongxin.Owts.XpertPressBridge/Xiongxin.Owts.XpertPressBridge.csproj"
dotnet build "$PROJECT" -c "$CFG" -f net48 --nologo
SRC="$ROOT/factory-press/Xiongxin.Owts.XpertPressBridge/bin/$CFG/net48"
DIST="$ROOT/factory-press/dist/net48"
DELIVER="$ROOT/deliverables/HV-DAC_Xpert_V1.1.2_factory-press-merge_$CFG"
mkdir -p "$DIST" "$DELIVER"
cp -a "$SRC"/. "$DIST"/
cp -a "$SRC"/*.dll "$DELIVER"/
if compgen -G "$SRC"/*.pdb > /dev/null; then
  cp -a "$SRC"/*.pdb "$DELIVER"/
fi
echo "Factory press net48: $DELIVER"
