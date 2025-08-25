#!/usr/bin/env bash
set -euo pipefail

# Resolve repo root
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

# Prefer locally-installed dotnet
if [ -x "$HOME/.dotnet/dotnet" ]; then
  export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
  export PATH="$DOTNET_ROOT:$PATH"
fi

# Optional: first arg is max results (default 5)
COUNT="${1:-5}"

exec dotnet run --project "$ROOT_DIR/src/youtube.csproj" -- --days 30 @ix9net "$COUNT"
