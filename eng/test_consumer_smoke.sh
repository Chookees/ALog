#!/usr/bin/env bash
# Tests the smoke counters without packing or starting the host.
# The green shape is one shared token and exit 0. The S4 shape (2 and 1) is red.

set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/.." && pwd)
cd "$root"
# shellcheck source=consumer-smoke.sh
source "$here/consumer-smoke.sh"

failures=0

# Prints a passing check name. Args: label. Returns 0.
pass() {
  printf 'ok %s\n' "$1"
}

# Runs a command that must fail. Args: label, then the command. Returns 0 either way.
expect_fail() {
  local label="$1"
  shift
  if "$@" >/dev/null 2>&1; then
    echo "FAIL ${label}: expected a non-zero status" >&2
    failures=$((failures + 1))
    return 0
  fi
  pass "$label"
}

# Runs a command that must succeed. Args: label, then the command. Returns 0 either way.
expect_ok() {
  local label="$1"
  shift
  if ! "$@" >/dev/null 2>&1; then
    echo "FAIL ${label}" >&2
    failures=$((failures + 1))
    return 0
  fi
  pass "$label"
}

# Writes one token per line. Args: path, token, count from 0 to 100. Returns 1 above 100.
write_tokens() {
  local path="$1"
  local token="$2"
  local count="$3"
  if [ "$count" -lt 0 ] || [ "$count" -gt 100 ]; then
    echo "write_tokens count must be from 0 to 100." >&2
    return 1
  fi
  : > "$path"
  local index=0
  while [ "$index" -lt "$count" ]; do
    printf '%s\n' "$token" >> "$path"
    index=$((index + 1))
  done
}

token_a=SMOKE-01234567-89ab-cdef-0123-456789abcdef
token_b=SMOKE-01234567-89ab-cdef-0123-456789abcdee
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

write_tokens "$work/same.txt" "$token_a" 2
same=$(token_stats "$work/same.txt")
if [ "$same" != "2 1" ]; then
  echo "FAIL same token stats: ${same}" >&2
  failures=$((failures + 1))
else
  pass "two copies one token"
fi

printf '%s\n%s\n' "$token_a" "$token_b" > "$work/two.txt"
two=$(token_stats "$work/two.txt")
if [ "$two" != "2 2" ]; then
  echo "FAIL two token stats: ${two}" >&2
  failures=$((failures + 1))
else
  pass "two distinct tokens"
fi

write_tokens "$work/hundred.txt" "$token_a" 100
expect_ok "100 tokens" token_stats "$work/hundred.txt"

python3 - "$work/too-many.txt" "$token_a" << 'PY'
import sys
path, token = sys.argv[1], sys.argv[2]
with open(path, "w", encoding="utf-8") as handle:
    for index in range(101):
        handle.write(token + "\n")
        if index > 101:
            break
PY
expect_fail "101 tokens" token_stats "$work/too-many.txt"

python3 - "$work/big.bin" << 'PY'
import sys
with open(sys.argv[1], "wb") as handle:
    written = handle.write(b"x" * 1000001)
    if written != 1000001:
        raise SystemExit("short write")
PY
expect_fail "file over 1000000 bytes" token_stats "$work/big.bin"

expect_fail "stdout 2 file 1 is red" evaluate_smoke "2 1 1 1 1 0"
expect_ok "one and one is green" evaluate_smoke "1 1 1 1 1 0"
expect_fail "non-zero exit is red" evaluate_smoke "1 1 1 1 1 1"

summary=$(write_smoke_summary "2 1 1 1 1 0" "$work/summary.md")
if ! printf '%s\n' "$summary" | grep -F -q "stdout+stderr count: 2"; then
  echo "FAIL summary missing stdout count" >&2
  failures=$((failures + 1))
else
  pass "summary stdout count"
fi
if ! printf '%s\n' "$summary" | grep -F -q "file count: 1"; then
  echo "FAIL summary missing file count" >&2
  failures=$((failures + 1))
else
  pass "summary file count"
fi
if ! grep -F -q "stdout+stderr count: 2" "$work/summary.md"; then
  echo "FAIL summary file was not appended" >&2
  failures=$((failures + 1))
else
  pass "summary appended"
fi

export ALog__MinimumLevel=Debug
clear_inherited_alog_keys
if [ -n "${ALog__MinimumLevel+x}" ]; then
  echo "FAIL inherited ALog key was kept" >&2
  failures=$((failures + 1))
else
  pass "inherited ALog key cleared"
fi
if [ -z "${PATH:-}" ]; then
  echo "FAIL PATH was cleared" >&2
  failures=$((failures + 1))
else
  pass "PATH kept"
fi

index=0
while [ "$index" -lt 51 ]; do
  export "ALog__K${index}=1"
  index=$((index + 1))
done
expect_fail "51 inherited keys" clear_inherited_alog_keys
if [ -z "${ALog__K0+x}" ]; then
  echo "FAIL over-cap clear removed a key" >&2
  failures=$((failures + 1))
else
  pass "over-cap clear removed nothing"
fi
index=0
while [ "$index" -lt 51 ]; do
  unset "ALog__K${index}"
  index=$((index + 1))
done

version=$(read_csproj_version "ALog/ALog.csproj")
ext=$(read_csproj_version "ALog.Extensions.Logging/ALog.Extensions.Logging.csproj")
if [ "$version" != "0.2.1" ] || [ "$version" != "$ext" ]; then
  echo "FAIL versions alog=${version} extensions=${ext}" >&2
  failures=$((failures + 1))
else
  pass "package version 0.2.1"
fi

expect_ok "consumer project" assert_consumer_project "$CONSUMER_PROJECT" "$version"

cat > "$work/bad.csproj" << EOF
<Project>
  <ItemGroup>
    <ProjectReference Include="ALog/ALog.csproj" />
    <PackageReference Include="ALog.Extensions.Logging" Version="${version}" />
  </ItemGroup>
</Project>
EOF
expect_fail "missing smoke switch" assert_consumer_project "$work/bad.csproj" "$version"

assets="$work/assets.json"
feed="$work/feed"
mkdir -p "$feed"
printf 'packed-bytes' > "$feed/ALog.${version}.nupkg"
sha=$(python3 - "$feed/ALog.${version}.nupkg" << 'PY'
import base64, hashlib, sys
print(base64.b64encode(hashlib.sha512(open(sys.argv[1], "rb").read()).digest()).decode("ascii"))
PY
)
cat > "$assets" << EOF
{"libraries":{"ALog/${version}":{"type":"package","sha512":"${sha}"},"ALog.Extensions.Logging/${version}":{"type":"package"}},"sources":{"${feed}":{}}}
EOF
proof=$(python3 eng/smoke-assets-check.py "$assets" "$version" "$feed")
if [ "$proof" != "SMOKE-PACKAGE ALog ${version} from-feed" ]; then
  echo "FAIL package proof: ${proof}" >&2
  failures=$((failures + 1))
else
  pass "package proof line"
fi
cat > "$assets" << EOF
{"libraries":{"ALog/1.0.0":{"type":"project","path":"ALog/ALog.csproj"}},"sources":{"${feed}":{}}}
EOF
expect_fail "project assets entry" python3 eng/smoke-assets-check.py "$assets" "$version" "$feed"

if [ "$failures" -ne 0 ]; then
  echo "${failures} consumer-smoke tests failed" >&2
  exit 1
fi
echo "consumer-smoke tests passed"
