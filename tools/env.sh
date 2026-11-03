#!/usr/bin/env bash
# Sourced by the other scripts: puts a .NET 10 SDK on PATH, from ~/.dotnet if the system's is older.
_has_net10() { [[ -x "$1" ]] && "$1" --list-sdks 2>/dev/null | grep -q '^10\.'; }
if _has_net10 "$HOME/.dotnet/dotnet"; then
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
elif ! _has_net10 "$(command -v dotnet 2>/dev/null)"; then
    echo "error: no .NET 10 SDK found. Install one with:" >&2
    echo "  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir \$HOME/.dotnet" >&2
    return 1 2>/dev/null || exit 1
fi
