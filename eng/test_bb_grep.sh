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

# Records a miss unless one project file contains one exact element.
# The license guard uses this so a missing SPDX expression, copyright line, or
# NOTICE pack item names the file and the text that was supposed to ship.
# Args: project path, exact text, check name. Returns 0. Empty args are a miss.
# A miss increments the failure count and leaves the later checks running.
require_project_text() {
  local path="$1"
  local text="$2"
  local name="$3"
  if [ -z "${path}" ] || [ -z "${text}" ] || [ -z "${name}" ]; then
    fail "license guard require_project_text needs a path, expected text, and a name"
    return 0
  fi
  if [ ! -f "${path}" ]; then
    fail "${path}: expected ${text} (${name}); file is missing"
    return 0
  fi
  if ! grep -F -q -- "${text}" "${path}"; then
    fail "${path}: expected ${text} (${name})"
    return 0
  fi
  pass "${name}"
}

# Confirms both shipped projects declare the Apache-2.0 SPDX expression.
# That expression is what NuGet copies into the nuspec. Any other value would
# publish the package under a different license while LICENSE stays Apache-2.0.
# Args: none. Returns 0. Each project is named in its own pass or fail line.
check_apache_license_expression() {
  local expression='<PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>'
  local projects=(
    'ALog/ALog.csproj'
    'ALog.Extensions.Logging/ALog.Extensions.Logging.csproj'
  )
  local index=0
  local path
  while [ "${index}" -lt 2 ]; do
    path="${projects[${index}]}"
    index=$((index + 1))
    require_project_text "${path}" "${expression}" "Apache-2.0 license expression"
  done
}

# Confirms both shipped projects pack the repository NOTICE into the nupkg root.
# Without that item the package can ship Apache-2.0 terms and omit the notice
# Apache 2.0 section 4(d) requires. Args: none. Returns 0.
check_notice_pack_item() {
  local item='<None Include="..\NOTICE" Pack="true" PackagePath="\" />'
  local projects=(
    'ALog/ALog.csproj'
    'ALog.Extensions.Logging/ALog.Extensions.Logging.csproj'
  )
  local index=0
  local path
  while [ "${index}" -lt 2 ]; do
    path="${projects[${index}]}"
    index=$((index + 1))
    require_project_text "${path}" "${item}" "NOTICE pack item"
  done
}

# Confirms NOTICE is exactly "ALog" and "Copyright 2026 Artur Bobb" in LF.
# Both nupkgs copy this file byte for byte, so a BOM, CR, or extra line would
# ship a different notice than the one reviewed here. Args: none. Returns 0.
check_notice_bytes() {
  local path='NOTICE'
  local expected
  if [ ! -f "${path}" ]; then
    fail "NOTICE: expected two LF lines 'ALog' and 'Copyright 2026 Artur Bobb'; file is missing"
    return 0
  fi
  expected=$(mktemp)
  printf 'ALog\nCopyright 2026 Artur Bobb\n' > "${expected}"
  if ! cmp -s -- "${expected}" "${path}"; then
    fail "NOTICE: expected exactly two LF lines 'ALog' and 'Copyright 2026 Artur Bobb', no BOM and no CR"
  else
    pass "NOTICE bytes"
  fi
  rm -f -- "${expected}"
}

# Confirms LICENSE is still the Apache-2.0 text pinned from commit 695c125.
# The pin is sha256 cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30.
# A rewritten LICENSE would change the terms without this check noticing the words.
# Args: none. Returns 0. A missing file or a different hash is a miss.
check_license_sha256() {
  local expected='cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30'
  local actual
  if [ ! -f LICENSE ]; then
    fail "LICENSE: expected sha256 ${expected}; file is missing"
    return 0
  fi
  actual=$(sha256sum -- LICENSE)
  actual="${actual%% *}"
  if [ "${actual}" != "${expected}" ]; then
    fail "LICENSE: expected sha256 ${expected}, found ${actual}"
    return 0
  fi
  pass "LICENSE sha256"
}

# Confirms the tracked tree never sets PackageLicenseUrl or PackageLicenseFile.
# Either property would replace or fight the SPDX expression. This script is
# excluded because it has to name both properties in order to search for them.
# Args: none. Returns 0. git grep status 1 means zero hits, which is the pass.
check_no_legacy_license_properties() {
  local hits
  local status
  set +e
  hits=$(git grep -n -F -e 'PackageLicenseUrl' -e 'PackageLicenseFile' -- . ':(exclude)eng/test_bb_grep.sh')
  status=$?
  set -e
  if [ "${status}" -eq 1 ]; then
    pass "no PackageLicenseUrl or PackageLicenseFile"
    return 0
  fi
  if [ "${status}" -ne 0 ]; then
    fail "git grep for PackageLicenseUrl and PackageLicenseFile failed with status ${status}"
    return 0
  fi
  fail "expected PackageLicenseUrl and PackageLicenseFile zero times outside eng/test_bb_grep.sh: ${hits}"
}

# Confirms both shipped projects set the copyright NuGet writes into the nuspec.
# The notice file and the package metadata must name the same owner, Artur Bobb,
# for 2026. Args: none. Returns 0.
check_package_copyright() {
  local copyright='<Copyright>Copyright 2026 Artur Bobb</Copyright>'
  local projects=(
    'ALog/ALog.csproj'
    'ALog.Extensions.Logging/ALog.Extensions.Logging.csproj'
  )
  local index=0
  local path
  while [ "${index}" -lt 2 ]; do
    path="${projects[${index}]}"
    index=$((index + 1))
    require_project_text "${path}" "${copyright}" "package copyright"
  done
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

authors='<Authors>Artur Bobb</Authors>'
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

check_apache_license_expression
check_notice_pack_item
check_notice_bytes
check_license_sha256
check_no_legacy_license_properties
check_package_copyright

if [ "$failures" -ne 0 ]; then
  echo "${failures} bb grep checks failed" >&2
  exit 1
fi
echo "bb grep tests passed"
