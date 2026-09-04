#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
LUBAN_DLL="$REPO_ROOT/Tools/Luban/Luban.dll"
CONFIG_FILE="$REPO_ROOT/Config/luban.conf"
CODE_DIR="$REPO_ROOT/Assets/Scripts/Generated/Config"
DATA_DIR="$REPO_ROOT/Assets/StreamingAssets/Config/Luban"

if [[ ! -f "$LUBAN_DLL" ]]; then
  echo "Luban tool is missing: $LUBAN_DLL" >&2
  exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo ".NET 8 Runtime is required by Luban v4.11.0." >&2
  exit 1
fi

if ! dotnet --list-runtimes | grep -q 'Microsoft\.NETCore\.App 8\.'; then
  echo ".NET 8 Runtime is required by Luban v4.11.0." >&2
  exit 1
fi

EXPECTED_CODE_DIR="$REPO_ROOT/Assets/Scripts/Generated/Config"
EXPECTED_DATA_DIR="$REPO_ROOT/Assets/StreamingAssets/Config/Luban"
if [[ "$CODE_DIR" != "$EXPECTED_CODE_DIR" || "$DATA_DIR" != "$EXPECTED_DATA_DIR" ]]; then
  echo "Refusing to clean unexpected generation directories." >&2
  exit 1
fi

GENERATION_ROOT="$(mktemp -d)"
trap 'rm -rf "$GENERATION_ROOT"' EXIT
GENERATED_CODE="$GENERATION_ROOT/code"
GENERATED_DATA="$GENERATION_ROOT/data"
mkdir -p "$GENERATED_CODE" "$GENERATED_DATA"

dotnet "$LUBAN_DLL" \
  -t client \
  -c cs-bin \
  -d bin \
  --conf "$CONFIG_FILE" \
  -x "outputCodeDir=$GENERATED_CODE" \
  -x "outputDataDir=$GENERATED_DATA"

sync_generated_directory() {
  local source="$1"
  local target="$2"
  mkdir -p "$target"

  while IFS= read -r -d '' target_file; do
    local relative="${target_file#"$target"/}"
    if [[ ! -f "$source/$relative" ]]; then
      rm -f "$target_file" "$target_file.meta"
    fi
  done < <(find "$target" -type f ! -name '*.meta' -print0)

  while IFS= read -r -d '' source_file; do
    local relative="${source_file#"$source"/}"
    local destination="$target/$relative"
    mkdir -p "$(dirname "$destination")"
    if [[ ! -f "$destination" ]] || ! cmp -s "$source_file" "$destination"; then
      cp "$source_file" "$destination"
    fi
  done < <(find "$source" -type f -print0)
}

sync_generated_directory "$GENERATED_CODE" "$CODE_DIR"
sync_generated_directory "$GENERATED_DATA" "$DATA_DIR"

echo "Luban generation completed."
