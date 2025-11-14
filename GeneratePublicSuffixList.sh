#!/usr/bin/env bash
set -euo pipefail

url="https://www.publicsuffix.org/list/public_suffix_list.dat"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
obj_dir="$script_dir/obj"
list_path="$obj_dir/public_suffix_list.dat"

mkdir -p "$obj_dir"

# Download if not exists
if [ ! -f "$list_path" ]; then
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL "$url" -o "$list_path"
  elif command -v wget >/dev/null 2>&1; then
    wget -q -O "$list_path" "$url"
  else
    echo "curl or wget required to download public suffix list" >&2
    exit 1
  fi
fi

exact=()
wildcards=()
exceptions=()

while IFS= read -r raw; do
  line="$raw"
  # Trim whitespace and CR
  line="$(printf '%s' "$line" | sed -E 's/^[[:space:]]+|[[:space:]]+$//g' | tr -d '\r')"
  [ -z "$line" ] && continue
  case "$line" in
    '//'*) continue ;;
    '!'*) exceptions+=("${line:1}") ;;
    '*.'*) wildcards+=("${line:2}") ;;
    *) exact+=("$line") ;;
  esac
done < "$list_path"

# Helper to print a set
print_set() {
  local arr=("$@")
  if [ ${#arr[@]} -eq 0 ]; then
    echo "    {"
    echo "    };"
    return
  fi
  for i in "${arr[@]}"; do
    printf '        "%s",\n' "$i"
  done
}

cs_file="$script_dir/PublicSuffixData.cs"

cat > "$cs_file" <<EOF
using System.Collections.Generic;

public static class PublicSuffixData
{
    public static readonly HashSet<string> Exact = new HashSet<string>
    {
$(print_set "${exact[@]}")
    };

    public static readonly HashSet<string> Wildcards = new HashSet<string>
    {
$(print_set "${wildcards[@]}")
    };

    public static readonly HashSet<string> Exceptions = new HashSet<string>
    {
$(print_set "${exceptions[@]}")
    };
}
EOF