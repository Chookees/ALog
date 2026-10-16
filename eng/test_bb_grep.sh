#!/usr/bin/env bash
# Static Build-Basis checks that do not need a compile.
# U-BB-01, U-BB-02, BB-04, BB-05, BB-07, BB-08, and the SQL comment.

set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/.." && pwd)
cd "$root"

failures=0

# Prints a passing check. Args: label.
pass() {
  printf 'ok %s\n' "$1"
}

# Records a failure. Args: message.
fail() {
  echo "FAIL $1" >&2
  failures=$((failures + 1))
}

if grep -E -n '\|x64\||\|x86' ALog.sln >/dev/null; then
  fail "solution still has x64 or x86"
else
  pass "no x64 or x86 in ALog.sln"
fi

if find . -name '*.slnx' -not -path './.git/*' | grep -q .; then
  fail "slnx file exists"
else
  pass "no slnx"
fi

if [ -e eng/alloc-budgets.json ]; then
  fail "alloc budget file exists"
else
  pass "no alloc budget file"
fi

authors='<Authors>Artur Zubert</Authors>'
if ! grep -F -q "$authors" ALog/ALog.csproj; then
  fail "ALog authors changed"
else
  pass "ALog authors"
fi
if ! grep -F -q "$authors" ALog.Extensions.Logging/ALog.Extensions.Logging.csproj; then
  fail "Extensions authors changed"
else
  pass "Extensions authors"
fi

if grep -R -n -E --exclude-dir=.git --exclude-dir=bin --exclude-dir=obj --exclude-dir=TestResults \
  --include='*.csproj' --include='*.props' --include='*.targets' \
  'IsTrimmable|IsAotCompatible' . >/dev/null; then
  fail "IsTrimmable or IsAotCompatible is set"
else
  pass "no trim or aot compatibility promise"
fi

if ! grep -F -q '"version": "10.0.402"' global.json; then
  fail "global.json version is not 10.0.402"
else
  pass "sdk pin 10.0.402"
fi
if ! grep -F -q '"rollForward": "disable"' global.json; then
  fail "rollForward is not disable"
else
  pass "rollForward disable"
fi

if ! grep -F -q 'package-ecosystem: dotnet-sdk' .github/dependabot.yml; then
  fail "dependabot dotnet-sdk missing"
else
  pass "dependabot dotnet-sdk"
fi

if ! grep -F -q 'global-json-file: global.json' .github/workflows/dotnet.yml; then
  fail "setup-dotnet does not read global.json"
else
  pass "workflow reads global.json"
fi

if ! grep -F -q 'Only the value 1 enables the SQL live tests.' .github/workflows/dotnet.yml; then
  fail "ALOG_REQUIRE_SQL comment missing"
else
  pass "only the value 1 enables SQL"
fi

if grep -F -q 'CoverageMetadata' ALog.sln; then
  fail "metadata tool is in the solution"
else
  pass "metadata tool is outside the solution"
fi

if ! grep -F -q '<IsPackable>false</IsPackable>' eng/coverage-metadata/CoverageMetadata.csproj; then
  fail "metadata tool is packable"
else
  pass "metadata tool is not packed"
fi

if ! grep -F -q '# analyzer-ratchet (BB): bestehende Befunde, nur schrumpfen' .editorconfig; then
  fail "ratchet marker missing"
else
  pass "ratchet marker"
fi

if ! grep -F -q "TreatWarningsAsErrors Condition=\"'\$(CI)' == 'true'\"" Directory.Build.props; then
  fail "CI warning gate missing"
else
  pass "warnings are errors only when CI=true"
fi

if ! grep -F -q 'SMOKE-PACKAGE ALog.Extensions.Logging' eng/smoke-assets-check.py; then
  fail "extensions feed proof missing"
else
  pass "extensions feed proof"
fi

if grep -F -q 'console gate: SMOKE-CONSOLE count=' eng/consumer-smoke.sh; then
  fail "smoke summary still prints the console count token"
else
  pass "smoke summary has no console count token"
fi

if [ "$failures" -ne 0 ]; then
  echo "${failures} bb grep checks failed" >&2
  exit 1
fi
echo "bb grep tests passed"
