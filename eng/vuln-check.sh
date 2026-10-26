#!/usr/bin/env bash
# Fails the CI job when `dotnet list` reports a vulnerable package.
#
# arc_spec 4.1 point 2: the command exits 0 even when it finds vulnerabilities
# (checked on SDK 10.0.403). This script fails on the sentence
# "has the following vulnerable packages" and also on an empty report or a
# non-zero exit. Run it from the repository root. Next: .github/workflows/dotnet.yml.

set -euo pipefail

# Returns 0 when the report has text and does not list vulnerable packages.
# Args: the full dotnet list output. Empty or whitespace-only text is a failure
# because a crashed command must not look like a clean bill. Returns 1 otherwise.
report_is_clean() {
  local report="$1"
  if [ -z "${report//[[:space:]]/}" ]; then
    echo "Vulnerability report was empty. dotnet list produced no output." >&2
    return 1
  fi
  if printf '%s\n' "$report" | grep -F -q "has the following vulnerable packages"; then
    echo "Vulnerable packages are listed above. dotnet list exits 0 when it finds them, so this script checks the text." >&2
    return 1
  fi
}

# Runs the solution vulnerability list and applies report_is_clean.
# Prints the report first so a red log shows the package, version, and severity.
# Returns 1 when the text lists vulnerabilities, or the command status when it failed cleanly.
main() {
  if [ ! -f ALog.sln ]; then
    echo "Run vuln-check.sh from the repository root. ALog.sln is missing." >&2
    return 1
  fi
  local report
  local status=0
  # The vulnerable-package sentence is English. Force that language so a runner
  # whose default is German cannot hide the finding.
  export DOTNET_CLI_UI_LANGUAGE=en
  set +e
  report=$(dotnet list ALog.sln package --vulnerable --include-transitive 2>&1)
  status=$?
  set -e
  printf '%s\n' "$report"
  if ! report_is_clean "$report"; then
    return 1
  fi
  if [ "$status" -ne 0 ]; then
    echo "dotnet list failed with exit ${status} and did not list vulnerable packages." >&2
    return "$status"
  fi
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  main "$@"
fi
