#!/usr/bin/env bash
set -euo pipefail

TFM="${OTAPI_TFM:-net11.0}"
PATCH_TARGET="${1:-p}"

case "$PATCH_TARGET" in
  p) ;;
  m|t)
    echo "Patch target '$PATCH_TARGET' is not part of the validated VegaKernel .NET 11 server baseline." >&2
    echo "Use 'p' for the PC server path." >&2
    exit 2
    ;;
  *)
    echo "Unknown patch target '$PATCH_TARGET'. Expected: p" >&2
    exit 2
    ;;
esac

dotnet restore OTAPI.Mods.slnf
dotnet restore OTAPI.Server.Launcher.slnf
dotnet build OTAPI.Mods.slnf --no-restore

pushd "OTAPI.Patcher/bin/Debug/$TFM" >/dev/null
dotnet run --project ../../../OTAPI.Patcher.csproj --no-build -- -patchTarget="$PATCH_TARGET" -latest=n -framework="$TFM"
popd >/dev/null

dotnet build OTAPI.Server.Launcher.slnf --no-restore

pushd "OTAPI.Server.Launcher/bin/Debug/$TFM" >/dev/null
dotnet OTAPI.Server.Launcher.dll -test-init
popd >/dev/null
