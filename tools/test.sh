#!/usr/bin/env bash
#
# The protocol and the server, driven over loopback. No game, and no KSA assemblies, needed.
#
#     ./tools/test.sh
#     ./tools/test.sh --filter ServerTests
#
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=env.sh
source "$HERE/tools/env.sh"
dotnet test "$HERE/tests/KSAMultiplayer.Tests.csproj" -c Release --nologo "$@"
