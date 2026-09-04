#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
LUBAN_DLL="$REPO_ROOT/Tools/Luban/Luban.dll"
CONFIG_FILE="$REPO_ROOT/Config/luban.conf"
COMMITTED_CODE="$REPO_ROOT/Assets/Scripts/Generated/Config"
COMMITTED_DATA="$REPO_ROOT/Assets/StreamingAssets/Config/Luban"
VALIDATION_ROOT="$(mktemp -d)"
trap 'rm -rf "$VALIDATION_ROOT"' EXIT

mkdir -p "$VALIDATION_ROOT/code" "$VALIDATION_ROOT/data"

dotnet "$LUBAN_DLL" \
  -t client \
  -c cs-bin \
  -d bin \
  --conf "$CONFIG_FILE" \
  -x "outputCodeDir=$VALIDATION_ROOT/code" \
  -x "outputDataDir=$VALIDATION_ROOT/data"

normalize_code_tree() {
  local source="$1"
  local target="$2"

  while IFS= read -r -d '' source_file; do
    local relative="${source_file#"$source"/}"
    local destination="$target/$relative"
    mkdir -p "$(dirname "$destination")"
    tr -d '\r' < "$source_file" > "$destination"
  done < <(find "$source" -type f ! -name '*.meta' -print0)
}

copy_data_tree() {
  local source="$1"
  local target="$2"

  while IFS= read -r -d '' source_file; do
    local relative="${source_file#"$source"/}"
    local destination="$target/$relative"
    mkdir -p "$(dirname "$destination")"
    cp "$source_file" "$destination"
  done < <(find "$source" -type f ! -name '*.meta' -print0)
}

mkdir -p \
  "$VALIDATION_ROOT/committed-code" \
  "$VALIDATION_ROOT/generated-code" \
  "$VALIDATION_ROOT/committed-data" \
  "$VALIDATION_ROOT/generated-data"

normalize_code_tree "$COMMITTED_CODE" "$VALIDATION_ROOT/committed-code"
normalize_code_tree "$VALIDATION_ROOT/code" "$VALIDATION_ROOT/generated-code"
copy_data_tree "$COMMITTED_DATA" "$VALIDATION_ROOT/committed-data"
copy_data_tree "$VALIDATION_ROOT/data" "$VALIDATION_ROOT/generated-data"

diff -ru "$VALIDATION_ROOT/committed-code" "$VALIDATION_ROOT/generated-code"
diff -ru "$VALIDATION_ROOT/committed-data" "$VALIDATION_ROOT/generated-data"

echo "Luban validation completed. Committed outputs match Excel sources."
