#!/usr/bin/env bash
# Prints the Build-Basis MSBuild properties for every solution project.
# BB-01: EnforceCodeStyleInBuild is true for each project.
# BB-02: TreatWarningsAsErrors is true only when CI=true, and only for the
# libraries, the tests, and the consumer. DemoApp, the sample, and the
# benchmarks stay false (DC-Z2) even when CI=true.
# BB-07: the two libraries enable the trim, AOT, and single-file analyzers,
# and neither IsTrimmable nor IsAotCompatible is true.
# A mismatch exits 1. The lines are the log the pull request quotes.

set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/.." && pwd)
cd "$root"

failures=0

# Prints a property value. Args: ci|local, project path, property name.
# Prints the last non-empty line. Returns 1 when msbuild fails.
read_property() {
  local mode="$1"
  local project="$2"
  local name="$3"
  local output
  if [ "$mode" = "ci" ]; then
    output=$(CI=true dotnet msbuild "$project" -getProperty:"$name" -nologo -v:q </dev/null) || return 1
  else
    output=$(env -u CI dotnet msbuild "$project" -getProperty:"$name" -nologo -v:q </dev/null) || return 1
  fi
  printf '%s\n' "$output" | awk 'NF { last=$0 } END { print last }'
}

# Compares one property with the expected value. Args: label, actual, expected.
# Returns 0 either way and increments failures on a mismatch.
expect_value() {
  local label="$1"
  local actual="$2"
  local expected="$3"
  printf '%s=%s\n' "$label" "$actual"
  if [ "$actual" != "$expected" ]; then
    echo "FAIL ${label}: expected ${expected}" >&2
    failures=$((failures + 1))
  fi
}

# Reads one property and compares it. Args: mode, project, property, expected, label.
check_property() {
  local mode="$1"
  local project="$2"
  local name="$3"
  local expected="$4"
  local label="$5"
  local actual
  if ! actual=$(read_property "$mode" "$project" "$name"); then
    echo "FAIL ${label}: msbuild failed" >&2
    failures=$((failures + 1))
    return 0
  fi
  expect_value "$label" "$actual" "$expected"
}

# Projects that keep TreatWarningsAsErrors when CI=true.
# DemoApp, the samples, and the benchmarks are the DC-Z2 opt-out and are not listed here.
gated='ALog/ALog.csproj
ALog.Extensions.Logging/ALog.Extensions.Logging.csproj
ALog.Tests/ALog.Tests.csproj
eng/consumer-smoke/console/ALog.ConsumerSmoke.csproj'
libraries='ALog/ALog.csproj
ALog.Extensions.Logging/ALog.Extensions.Logging.csproj'

while IFS= read -r project; do
  [ -n "$project" ] || continue
  check_property local "$project" EnforceCodeStyleInBuild true "${project} EnforceCodeStyleInBuild"
  if printf '%s\n' "$gated" | grep -F -x -q "$project"; then
    check_property ci "$project" TreatWarningsAsErrors true "${project} TreatWarningsAsErrors CI=true"
    check_property local "$project" TreatWarningsAsErrors false "${project} TreatWarningsAsErrors CI unset"
  else
    check_property ci "$project" TreatWarningsAsErrors false "${project} TreatWarningsAsErrors CI=true (DC-Z2)"
    check_property local "$project" TreatWarningsAsErrors false "${project} TreatWarningsAsErrors CI unset (DC-Z2)"
  fi
  if printf '%s\n' "$libraries" | grep -F -x -q "$project"; then
    check_property local "$project" EnableTrimAnalyzer true "${project} EnableTrimAnalyzer"
    check_property local "$project" EnableAotAnalyzer true "${project} EnableAotAnalyzer"
    check_property local "$project" EnableSingleFileAnalyzer true "${project} EnableSingleFileAnalyzer"
  else
    check_property local "$project" EnableTrimAnalyzer "" "${project} EnableTrimAnalyzer"
    check_property local "$project" EnableAotAnalyzer "" "${project} EnableAotAnalyzer"
    check_property local "$project" EnableSingleFileAnalyzer "" "${project} EnableSingleFileAnalyzer"
  fi
  check_property local "$project" IsTrimmable "" "${project} IsTrimmable"
  check_property local "$project" IsAotCompatible "" "${project} IsAotCompatible"
done << EOF
ALog/ALog.csproj
ALog.Extensions.Logging/ALog.Extensions.Logging.csproj
ALog.Tests/ALog.Tests.csproj
eng/consumer-smoke/console/ALog.ConsumerSmoke.csproj
DemoApp/DemoApp.csproj
samples/ALog.AspNetSample/ALog.AspNetSample.csproj
samples/ALog.QuickstartSample/ALog.QuickstartSample.csproj
ALog.Benchmarks/ALog.Benchmarks.csproj
EOF

if [ "$failures" -ne 0 ]; then
  echo "${failures} build property checks failed" >&2
  exit 1
fi
echo "build property proof passed"
