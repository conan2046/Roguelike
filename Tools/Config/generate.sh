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
trap 'rm -rf "$GENERATION_ROOT" 2>/dev/null || true' EXIT
GENERATED_CODE="$GENERATION_ROOT/code"
GENERATED_DATA="$GENERATION_ROOT/data"
mkdir -p "$GENERATED_CODE" "$GENERATED_DATA"

# Git Bash 传入 Unix 风格路径时，Windows 版 dotnet 会把它当作项目目录解析，
# 并因仓库根目录存在多个 csproj/slnx 而报 MSB1011；必须先转成 Windows 原生路径。
to_win() { cygpath -w "$1" 2>/dev/null || echo "$1"; }

dotnet "$(to_win "$LUBAN_DLL")" \
  -t client \
  -c cs-bin \
  -d bin \
  --conf "$(to_win "$CONFIG_FILE")" \
  -x "outputCodeDir=$(to_win "$GENERATED_CODE")" \
  -x "outputDataDir=$(to_win "$GENERATED_DATA")"

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
