#!/usr/bin/env bash
# Packs ALog and runs the console consumer that arc_spec 4.1 point 7 describes.
# Usage: eng/consumer-smoke.sh file|console
#
# Both modes restore with -p:ALogSmokeFromFeed=true and print
# SMOKE-PACKAGE ALog <version> from-feed and the same line for
# ALog.Extensions.Logging after checking project.assets.json and both SHA-512s.
# file is green when SMOKE-<guid> appears once in the ALog file and the process
# exits 0. console prints SMOKE-CONSOLE count=<n> and is green only when n is 1.
# ALog does not add a console writer unless ALog:Console:Enabled is true, so the
# Microsoft console provider writes the line once. The normal solution build uses the
# project reference; only this script selects the packed package.

set -euo pipefail

readonly SMOKE_TOKEN_PATTERN='SMOKE-[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}'
readonly CONSUMER_PROJECT="eng/consumer-smoke/console/ALog.ConsumerSmoke.csproj"

# Fails when the script is not started from the repository root.
# Returns 0 when ALog.sln is in the working directory.
require_repo_root() {
  if [ ! -f ALog.sln ]; then
    echo "Run consumer-smoke.sh from the repository root. ALog.sln is missing." >&2
    return 1
  fi
}

# Prints the single <Version> text from a csproj.
# Args: project path. Returns 1 when the file is missing and 2 when the count is not one.
# Does not print the version on failure.
read_csproj_version() {
  local path="$1"
  if [ ! -f "$path" ]; then
    echo "Project file not found: ${path}" >&2
    return 1
  fi
  python3 - "$path" << 'PY'
import sys
import xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
found = []
for element in root.iter():
    if element.tag.endswith("Version") and element.text and element.text.strip():
        found.append(element.text.strip())
if len(found) != 1:
    sys.stderr.write("expected one Version element\n")
    sys.exit(2)
sys.stdout.write(found[0])
PY
}

# Checks the consumer can build from the solution and from the local feed.
# Args: csproj path, expected package version. Returns non-zero when the smoke
# switch, the project reference, the package version, or an NU1901-NU1904 suppression is wrong.
assert_consumer_project() {
  local path="$1"
  local expected="$2"
  python3 - "$path" "$expected" << 'PY'
import sys
import xml.etree.ElementTree as ET
path, expected = sys.argv[1], sys.argv[2]
raw = open(path, encoding="utf-8").read()
if "'$(ALogSmokeFromFeed)' == 'true'" not in raw or "'$(ALogSmokeFromFeed)' != 'true'" not in raw:
    sys.stderr.write("consumer is missing the ALogSmokeFromFeed switch\n")
    sys.exit(1)
root = ET.parse(path).getroot()
found_package = False
found_project = False
for element in root.iter():
    if element.tag.endswith("ProjectReference"):
        found_project = True
    if element.tag.endswith("PackageReference") and element.attrib.get("Include") == "ALog.Extensions.Logging":
        found_package = True
        version = element.attrib.get("Version", "")
        if version != expected:
            sys.stderr.write(f"consumer version {version} != {expected}\n")
            sys.exit(1)
text = ET.tostring(root, encoding="unicode")
for code in ("NU1901", "NU1902", "NU1903", "NU1904"):
    if code in text:
        sys.stderr.write(f"consumer mentions {code}\n")
        sys.exit(1)
if not found_package or not found_project:
    sys.stderr.write("consumer needs both a package reference and a project reference\n")
    sys.exit(1)
PY
}

# Creates the feed and package-cache directories and prints the work path.
# NUGET_PACKAGES is pointed at that cache so an older ALog package cannot be reused.
# Returns 1 when mktemp fails. Messages go to stderr; stdout is only the path.
prepare_work_dir() {
  local base="${RUNNER_TEMP:-/tmp}"
  local work
  work=$(mktemp -d "${base}/alog-smoke.XXXXXX")
  mkdir -p "$work/feed" "$work/packages"
  printf '%s\n' "$work"
}

# Writes a NuGet config that forces ALog packages to come from the local feed.
# Args: config path, feed directory. Returns 1 when the feed path is unsafe for XML.
write_nuget_config() {
  local config="$1"
  local feed="$2"
  case "$feed" in
    *'<'*|*'>'*|*\&*|*'"'*|*"'"*)
      echo "Feed path cannot be written into NuGet config." >&2
      return 1
      ;;
  esac
  cat > "$config" << EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="alog-local" value="${feed}" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="alog-local">
      <package pattern="ALog" />
      <package pattern="ALog.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
EOF
}

# Packs both packages into the work feed and writes the NuGet config.
# Args: work directory, package version. Returns 1 when a nupkg is missing.
pack_packages() {
  local work="$1"
  local version="$2"
  dotnet pack ALog/ALog.csproj -c Release -o "$work/feed" --nologo
  dotnet pack ALog.Extensions.Logging/ALog.Extensions.Logging.csproj -c Release -o "$work/feed" --nologo
  if [ ! -f "$work/feed/ALog.${version}.nupkg" ]; then
    echo "ALog package ${version} was not produced." >&2
    return 1
  fi
  if [ ! -f "$work/feed/ALog.Extensions.Logging.${version}.nupkg" ]; then
    echo "Extensions package ${version} was not produced." >&2
    return 1
  fi
  write_nuget_config "$work/nuget.config" "$work/feed"
}

# Restores the consumer from the local feed with the package reference selected.
# Args: work directory. Removes the previous obj directory so a project-reference
# assets file cannot be reused. Returns the dotnet restore status.
restore_consumer_from_feed() {
  local work="$1"
  rm -rf eng/consumer-smoke/console/obj
  dotnet restore "$CONSUMER_PROJECT" -p:ALogSmokeFromFeed=true --configfile "$work/nuget.config" --nologo
}

# Builds the already restored feed-based consumer.
# Args: work directory. The property stays on so the build cannot switch back
# to the project reference. Returns the dotnet build status.
build_consumer() {
  local work="$1"
  dotnet build "$CONSUMER_PROJECT" -c Release -p:ALogSmokeFromFeed=true --no-restore --nologo
}

# Proves project.assets.json resolved ALog as the packed package from the feed.
# Args: assets path, package version, feed directory. Prints the SMOKE-PACKAGE line.
# Returns 1 when ALog is missing, a project reference, or not from that feed.
prove_feed_package() {
  python3 eng/smoke-assets-check.py "$1" "$2" "$3"
}

# Accepts the two smoke jobs. Args: file or console. Returns 1 for anything else.
require_smoke_mode() {
  case "$1" in
    file|console) return 0 ;;
  esac
  echo "Usage: eng/consumer-smoke.sh file|console" >&2
  return 1
}

# File job gate: one distinct token in the ALog file and consumer exit 0.
# Args: file count, distinct file count, consumer exit status.
evaluate_file_gate() {
  local file_count="$1"
  local file_distinct="$2"
  local app_status="$3"
  if [ "$app_status" -eq 0 ] && [ "$file_count" -eq 1 ] && [ "$file_distinct" -eq 1 ]; then
    return 0
  fi
  return 1
}

# Prints SMOKE-CONSOLE count=<n> and copies that exact line into the job summary.
# Args: count, summary path (empty skips the file).
report_console_count() {
  local count="$1"
  local summary_path="${2:-}"
  local line="SMOKE-CONSOLE count=${count}"
  printf '%s\n' "$line"
  if [ -n "$summary_path" ]; then
    printf '%s\n' "$line" >> "$summary_path"
  fi
}

# Drops inherited ALog__ variables so the consumer sees only the file sink.
# At most 50 names are accepted. More than that fails closed and unsets nothing.
# Returns 1 when the cap is exceeded.
clear_inherited_alog_keys() {
  local -a names=()
  local name
  while IFS= read -r name; do
    if [ -z "$name" ]; then
      continue
    fi
    if [ "${#names[@]}" -ge 50 ]; then
      echo "Refusing to clear more than 50 inherited ALog__ variables." >&2
      return 1
    fi
    names+=("$name")
  done < <(env | awk -F= '/^ALog__/ { print $1 }')
  local index=0
  while [ "$index" -lt "${#names[@]}" ] && [ "$index" -lt 50 ]; do
    unset "${names[$index]}"
    index=$((index + 1))
  done
}

# Prints "<count> <distinct>" for SMOKE guid tokens in a file.
# A missing file is 0 0. A file over 1000000 bytes or more than 100 tokens fails.
# Args: file path.
token_stats() {
  local file="$1"
  if [ -z "$file" ]; then
    echo "token_stats requires a file path." >&2
    return 1
  fi
  if [ ! -f "$file" ]; then
    printf '0 0\n'
    return 0
  fi
  local bytes
  bytes=$(wc -c < "$file" | tr -d ' ')
  if [ "$bytes" -gt 1000000 ]; then
    echo "Smoke log is larger than 1000000 bytes." >&2
    return 1
  fi
  local matches
  matches=$(grep -oE "$SMOKE_TOKEN_PATTERN" "$file" || true)
  if [ -z "$matches" ]; then
    printf '0 0\n'
    return 0
  fi
  local count distinct
  count=$(printf '%s\n' "$matches" | grep -c .)
  distinct=$(printf '%s\n' "$matches" | sort -u | grep -c .)
  if [ "$count" -gt 100 ]; then
    echo "Smoke token count ${count} exceeds 100." >&2
    return 1
  fi
  printf '%s %s\n' "$count" "$distinct"
}

# Prints the first SMOKE token in the file. Returns 1 when there is none.
# Args: file path.
first_token() {
  local file="$1"
  if [ ! -f "$file" ]; then
    return 1
  fi
  local matches
  matches=$(grep -oE "$SMOKE_TOKEN_PATTERN" "$file" || true)
  if [ -z "$matches" ]; then
    return 1
  fi
  printf '%s\n' "$matches" | head -n 1 || true
}

# Prints six fields: stdout count, stdout distinct, file count, file distinct,
# same-token (1 or 0), and the consumer exit status.
# Args: stdout log, ALog file, consumer exit status.
pack_stats() {
  local stdout_file="$1"
  local alog_file="$2"
  local app_status="$3"
  local stdout_stats file_stats
  stdout_stats=$(token_stats "$stdout_file")
  file_stats=$(token_stats "$alog_file")
  local stdout_count stdout_distinct file_count file_distinct
  read -r stdout_count stdout_distinct <<< "$stdout_stats"
  read -r file_count file_distinct <<< "$file_stats"
  local same=0
  if [ "$stdout_distinct" -eq 1 ] && [ "$file_distinct" -eq 1 ]; then
    local left right
    left=$(first_token "$stdout_file")
    right=$(first_token "$alog_file")
    if [ "$left" = "$right" ] && [ -n "$left" ]; then
      same=1
    fi
  fi
  printf '%s %s %s %s %s %s\n' \
    "$stdout_count" "$stdout_distinct" "$file_count" "$file_distinct" "$same" "$app_status"
}

# Returns 0 only for the green contract: one shared token on each side and exit 0.
# Args: the six-field string from pack_stats. Any other shape returns 1.
evaluate_smoke() {
  local packed="$1"
  local stdout_count stdout_distinct file_count file_distinct same_guid app_status
  read -r stdout_count stdout_distinct file_count file_distinct same_guid app_status <<< "$packed"
  case "$stdout_count$stdout_distinct$file_count$file_distinct$same_guid$app_status" in
    *[!0-9]*)
      echo "Smoke stats are not six integers." >&2
      return 1
      ;;
  esac
  if [ "$(printf '%s\n' "$packed" | awk '{print NF}')" -ne 6 ]; then
    echo "Smoke stats must have 6 fields." >&2
    return 1
  fi
  if [ "$app_status" -ne 0 ] || [ "$same_guid" -ne 1 ]; then
    return 1
  fi
  if [ "$stdout_count" -ne 1 ] || [ "$stdout_distinct" -ne 1 ]; then
    return 1
  fi
  if [ "$file_count" -ne 1 ] || [ "$file_distinct" -ne 1 ]; then
    return 1
  fi
}

# Appends the counts to the job summary and always prints them.
# Args: six-field stats, summary path (empty skips the file). The path's parent must exist.
write_smoke_summary() {
  local packed="$1"
  local summary_path="${2:-}"
  local stdout_count stdout_distinct file_count file_distinct same_guid app_status
  read -r stdout_count stdout_distinct file_count file_distinct same_guid app_status <<< "$packed"
  local text
  text=$(cat << EOF
## Consumer smoke

- stdout+stderr count: ${stdout_count}
- file count: ${file_count}
- distinct tokens (stdout+stderr / file): ${stdout_distinct} / ${file_distinct}
- same token in both: ${same_guid}
- consumer exit code: ${app_status}
- file gate: file count 1, one distinct file token, exit 0
- console gate: one stdout token and exit 0
EOF
)
  printf '%s\n' "$text"
  if [ -n "$summary_path" ]; then
    printf '%s\n' "$text" >> "$summary_path"
  fi
}

# Runs the built consumer. Only the file sink is configured, on purpose.
# Args: work directory. Stdout and stderr of the process go to combined.log.
# Returns the consumer's exit code.
run_consumer() {
  local work="$1"
  clear_inherited_alog_keys
  export ALog__File__Enabled=true
  export ALog__File__Path="$work/alog.log"
  : > "$work/combined.log"
  local status=0
  dotnet run --project "$CONSUMER_PROJECT" -c Release --no-build -p:ALogSmokeFromFeed=true >"$work/combined.log" 2>&1 || status=$?
  return "$status"
}

# Packs, proves the feed package, runs, and returns the status for one smoke mode.
# Args: file or console. A pack or restore failure still writes counts.
# file is green only for one ALog-file token and exit 0.
# console prints SMOKE-CONSOLE count=<n> and is green only when that count is 1.
main() {
  local mode="${1:-}"
  require_smoke_mode "$mode" || return 1
  require_repo_root
  export DOTNET_CLI_TELEMETRY_OPTOUT=1
  local version ext
  version=$(read_csproj_version "ALog/ALog.csproj")
  ext=$(read_csproj_version "ALog.Extensions.Logging/ALog.Extensions.Logging.csproj")
  if [ "$version" != "$ext" ]; then
    echo "ALog version ${version} does not match Extensions ${ext}." >&2
    return 1
  fi
  assert_consumer_project "$CONSUMER_PROJECT" "$version"
  local work
  work=$(prepare_work_dir)
  # prepare_work_dir runs in a subshell, so the package cache is exported here.
  export NUGET_PACKAGES="$work/packages"
  local app_status=1
  local proved=0
  if pack_packages "$work" "$version" && restore_consumer_from_feed "$work"; then
    if prove_feed_package "eng/consumer-smoke/console/obj/project.assets.json" "$version" "$work/feed"; then
      proved=1
      if build_consumer "$work"; then
        app_status=0
        run_consumer "$work" || app_status=$?
      fi
    fi
  fi
  local packed
  if ! packed=$(pack_stats "$work/combined.log" "$work/alog.log" "$app_status"); then
    packed="0 0 0 0 0 ${app_status}"
  fi
  local stdout_count file_count file_distinct
  read -r stdout_count _ file_count file_distinct _ _ <<< "$packed"
  write_smoke_summary "$packed" "${GITHUB_STEP_SUMMARY:-}"
  if [ "$mode" = "console" ]; then
    report_console_count "$stdout_count" "${GITHUB_STEP_SUMMARY:-}"
    if [ "$proved" -ne 1 ] || [ "$app_status" -ne 0 ] || [ "$stdout_count" -ne 1 ]; then
      return 1
    fi
    return 0
  fi
  if [ "$proved" -ne 1 ]; then
    return 1
  fi
  evaluate_file_gate "$file_count" "$file_distinct" "$app_status"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  main "$@"
fi
