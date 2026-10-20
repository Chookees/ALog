#!/usr/bin/env bash
# Checks packed ALog nupkgs, not symbol packages, against the repo license.
# Each package must carry the repository NOTICE and an Apache-2.0 nuspec
# whose copyright and authors name Artur Bobb. The nuspec must have one
# license element, and either no licenseUrl or the NuGet Apache-2.0 URL.
# A miss means the downloadable package would not match the license
# published in source.

set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/.." && pwd)
repo_notice="${root}/NOTICE"
work=""
seen_alog=0
seen_ext=0

# Deletes the temp directory used to unpack one nupkg.
# Leaving extracted package bytes behind would mix a later check with an
# earlier archive. Args: none. Returns 0. Safe when no directory was created.
cleanup_work() {
  if [ -z "${work}" ]; then
    return 0
  fi
  if [ ! -d "${work}" ]; then
    return 0
  fi
  rm -rf -- "${work}"
  work=""
}

trap cleanup_work EXIT

# Prints the only two ways to run this script.
# The argument-count check and the --dir check both call this, so the usage
# sentence cannot drift into two different texts. Args: none. Returns 1.
fail_usage() {
  printf '%s\n' "FAIL usage: eng/test_nupkg_license.sh --dir <pack-directory>  or  eng/test_nupkg_license.sh <ALog.nupkg> <ALog.Extensions.Logging.nupkg>" >&2
  return 1
}

# Rejects a call that is not two to eight nupkg paths.
# CI passes the ALog and ALog.Extensions.Logging packages. Fewer cannot name
# both, and more than eight is outside the fixed bound of this check.
# Args: the script arguments. Returns 1 when the count is out of range.
require_nupkg_count() {
  local count="$1"
  if [ -z "${count}" ]; then
    echo "FAIL nupkg license check needs a package count" >&2
    return 1
  fi
  if [ "${count}" -lt 2 ] || [ "${count}" -gt 8 ]; then
    fail_usage
    return 1
  fi
  return 0
}

# Rejects a path that is not a regular .nupkg file.
# Symbol packages (.snupkg) are not the packages consumers install, so they
# cannot satisfy the NOTICE or nuspec checks. Args: one path. Returns 1 when
# the path is missing, not a file, or not a .nupkg.
require_nupkg_file() {
  local nupkg="$1"
  if [ -z "${nupkg}" ]; then
    echo "FAIL nupkg path is empty" >&2
    return 1
  fi
  case "${nupkg}" in
    *.snupkg)
      echo "FAIL ${nupkg}: expected a .nupkg, not a symbol package" >&2
      return 1
      ;;
    *.nupkg) ;;
    *)
      echo "FAIL ${nupkg}: expected a .nupkg" >&2
      return 1
      ;;
  esac
  if [ ! -f "${nupkg}" ]; then
    echo "FAIL ${nupkg}: expected a packed .nupkg file" >&2
    return 1
  fi
  return 0
}

# Compares the package-root NOTICE with the repository NOTICE using cmp.
# Apache 2.0 section 4(d) requires the notice to travel with the package, and
# it must be the same bytes as the reviewed NOTICE. Args: nupkg path.
# Returns 1 when NOTICE is missing or differs.
require_notice_bytes() {
  local nupkg="$1"
  if [ -z "${nupkg}" ] || [ ! -f "${repo_notice}" ]; then
    echo "FAIL ${nupkg}: expected NOTICE at the package root, byte-identical to ${repo_notice}" >&2
    return 1
  fi
  if ! unzip -p -q -- "${nupkg}" NOTICE > "${work}/NOTICE" 2>"${work}/err"; then
    echo "FAIL ${nupkg}: expected NOTICE at the package root, byte-identical to ${repo_notice}" >&2
    return 1
  fi
  if ! cmp -s -- "${repo_notice}" "${work}/NOTICE"; then
    echo "FAIL ${nupkg}: NOTICE at the package root is not byte-identical to ${repo_notice}" >&2
    return 1
  fi
  return 0
}

# Extracts the single root nuspec into the work directory.
# License and copyright live on that root nuspec. A nested nuspec belongs to
# another package and must not pass this check. Args: nupkg path. Returns 1
# when listing fails, the archive has no root nuspec, or it has more than one.
# Stops after 200 zip entries.
extract_root_nuspec() {
  local nupkg="$1"
  local entry
  local seen=0
  local found=""
  if [ -z "${nupkg}" ] || [ ! -f "${nupkg}" ]; then
    echo "FAIL ${nupkg}: expected a nupkg file when looking for the nuspec" >&2
    return 1
  fi
  if ! unzip -Z1 -- "${nupkg}" > "${work}/list" 2>"${work}/err"; then
    echo "FAIL ${nupkg}: could not list zip entries to find the nuspec" >&2
    return 1
  fi
  while IFS= read -r entry; do
    seen=$((seen + 1))
    if [ "${seen}" -gt 200 ]; then
      echo "FAIL ${nupkg}: expected a root nuspec within the first 200 zip entries" >&2
      return 1
    fi
    case "${entry}" in
      */*) ;;
      *.nuspec)
        if [ -n "${found}" ]; then
          echo "FAIL ${nupkg}: expected one root nuspec, also found ${entry}" >&2
          return 1
        fi
        found="${entry}"
        ;;
    esac
  done < "${work}/list"
  if [ -z "${found}" ]; then
    echo "FAIL ${nupkg}: expected a .nuspec at the package root" >&2
    return 1
  fi
  if ! unzip -p -q -- "${nupkg}" "${found}" > "${work}/nuspec" 2>"${work}/err"; then
    echo "FAIL ${nupkg}: could not read root nuspec ${found}" >&2
    return 1
  fi
  printf '%s\n' "${found}"
}

# Prints the shipped package id from the extracted nuspec.
# License, licenseUrl, and authors failures name ALog or
# ALog.Extensions.Logging, so the id has to be known before those lines are
# judged. Args: nupkg path. Prints the id. Returns 1 when it is neither
# package. Reads ${work}/nuspec. The longer id is checked first so ALog is
# not treated as a prefix of ALog.Extensions.Logging.
read_package_id() {
  local nupkg="$1"
  if [ -z "${nupkg}" ] || [ ! -f "${work}/nuspec" ]; then
    echo "FAIL ${nupkg}: expected a nuspec id of ALog or ALog.Extensions.Logging" >&2
    return 1
  fi
  if grep -F -q -- '<id>ALog.Extensions.Logging</id>' "${work}/nuspec"; then
    printf '%s\n' "ALog.Extensions.Logging"
    return 0
  fi
  if grep -F -q -- '<id>ALog</id>' "${work}/nuspec"; then
    printf '%s\n' "ALog"
    return 0
  fi
  echo "FAIL ${nupkg}: nuspec id must be ALog or ALog.Extensions.Logging" >&2
  return 1
}

# Records which shipped package id this nuspec belongs to.
# The check is not done until both ALog and ALog.Extensions.Logging have been
# seen. Args: nupkg path. Returns 1 when the id is neither of those two.
# Reads ${work}/nuspec. Sets seen_alog or seen_ext.
note_package_id() {
  local nupkg="$1"
  local id
  id=$(read_package_id "${nupkg}") || return 1
  if [ "${id}" = "ALog.Extensions.Logging" ]; then
    seen_ext=1
    return 0
  fi
  seen_alog=1
  return 0
}

# Requires one exact nuspec element.
# The license expression and the copyright line are the package metadata
# consumers see. Args: nupkg path, nuspec entry name, exact element text.
# Returns 1 when the element is absent.
require_nuspec_element() {
  local nupkg="$1"
  local entry="$2"
  local element="$3"
  if [ -z "${nupkg}" ] || [ -z "${entry}" ] || [ -z "${element}" ]; then
    echo "FAIL ${nupkg}: nuspec element check needs a package, an entry, and the expected text" >&2
    return 1
  fi
  if ! grep -F -q -- "${element}" "${work}/nuspec"; then
    echo "FAIL ${nupkg}: ${entry} expected ${element}" >&2
    return 1
  fi
  return 0
}

# Requires exactly one opening <license> element in the extracted nuspec.
# <licenseUrl> starts with the same letters, so those characters are removed
# before the count. A second <license> would let the package advertise
# another license beside Apache-2.0. Args: nupkg path, package id. Returns 1
# when the count is not 1. Reads ${work}/nuspec. Stops after 200 lines and
# 8 opening tags on one line.
require_one_license_element() {
  local nupkg="$1"
  local pkg="$2"
  local line
  local rest
  local seen=0
  local count=0
  local hits=0
  if [ -z "${nupkg}" ] || [ -z "${pkg}" ] || [ ! -f "${work}/nuspec" ]; then
    echo "FAIL ${nupkg}: license element check needs a package, a package id, and an extracted nuspec" >&2
    return 1
  fi
  while IFS= read -r line || [ -n "${line}" ]; do
    seen=$((seen + 1))
    if [ "${seen}" -gt 200 ]; then
      echo "FAIL ${nupkg}: ${pkg}.nuspec license scan stopped at 200 lines" >&2
      return 1
    fi
    rest="${line//<licenseUrl/}"
    hits=0
    while [ "${hits}" -lt 8 ]; do
      case "${rest}" in
        *'<license'*) ;;
        *) break ;;
      esac
      rest="${rest#*<license}"
      count=$((count + 1))
      hits=$((hits + 1))
    done
    if [ "${hits}" -eq 8 ]; then
      case "${rest}" in
        *'<license'*)
          echo "FAIL ${nupkg}: ${pkg}.nuspec license scan stopped at 8 tags on one line" >&2
          return 1
          ;;
      esac
    fi
  done < "${work}/nuspec"
  if [ "${count}" -eq 1 ]; then
    return 0
  fi
  printf '%s\n' "FAIL ${nupkg}: ${pkg}.nuspec expected exactly one <license>, found ${count}" >&2
  return 1
}

url_seen=0
url_values=()

# Copies each <licenseUrl> value out of the extracted nuspec.
# A second URL is a failure even when the first is the Apache-2.0 address, so
# the check needs every value. Args: nupkg path, package id. Returns 1 when
# the scan hits its bound. Updates url_seen and url_values. Stops after 200
# lines and 8 URLs.
collect_license_urls() {
  local nupkg="$1"
  local pkg="$2"
  local line
  local rest
  local url
  local seen=0
  local hits=0
  if [ -z "${nupkg}" ] || [ -z "${pkg}" ] || [ ! -f "${work}/nuspec" ]; then
    echo "FAIL ${nupkg}: licenseUrl check needs a package, a package id, and an extracted nuspec" >&2
    return 1
  fi
  url_seen=0
  url_values=()
  while IFS= read -r line || [ -n "${line}" ]; do
    seen=$((seen + 1))
    if [ "${seen}" -gt 200 ]; then
      echo "FAIL ${nupkg}: ${pkg}.nuspec licenseUrl scan stopped at 200 lines" >&2
      return 1
    fi
    rest="${line}"
    hits=0
    while [ "${hits}" -lt 8 ]; do
      case "${rest}" in
        *'<licenseUrl>'*'</licenseUrl>'*) ;;
        *) break ;;
      esac
      if [ "${url_seen}" -ge 8 ]; then
        echo "FAIL ${nupkg}: ${pkg}.nuspec licenseUrl scan stopped at 8 urls" >&2
        return 1
      fi
      rest="${rest#*<licenseUrl>}"
      url="${rest%%</licenseUrl>*}"
      url_values[${url_seen}]="${url}"
      url_seen=$((url_seen + 1))
      rest="${rest#*</licenseUrl>}"
      hits=$((hits + 1))
    done
    if [ "${hits}" -eq 8 ]; then
      case "${rest}" in
        *'<licenseUrl>'*'</licenseUrl>'*)
          echo "FAIL ${nupkg}: ${pkg}.nuspec licenseUrl scan stopped at 8 urls on one line" >&2
          return 1
          ;;
      esac
    fi
  done < "${work}/nuspec"
  return 0
}

# Accepts no licenseUrl, or exactly one NuGet Apache-2.0 licenseUrl.
# NuGet writes that URL beside the license expression. A different URL, or a
# second URL, would point consumers at another license. The URL in a failure
# is the first value that is not that address, or the second value when every
# value is that address. Args: nupkg path, package id. Returns 1 on a bad or
# extra URL. Reads ${work}/nuspec through collect_license_urls.
require_apache_license_url() {
  local nupkg="$1"
  local pkg="$2"
  local expected="https://licenses.nuget.org/Apache-2.0"
  local index=0
  local reported=""
  if [ -z "${nupkg}" ] || [ -z "${pkg}" ]; then
    echo "FAIL ${nupkg}: licenseUrl check needs a package and a package id" >&2
    return 1
  fi
  collect_license_urls "${nupkg}" "${pkg}"
  if [ "${url_seen}" -eq 0 ]; then
    return 0
  fi
  if [ "${url_seen}" -eq 1 ] && [ "${url_values[0]}" = "${expected}" ]; then
    return 0
  fi
  reported="${url_values[0]}"
  while [ "${index}" -lt "${url_seen}" ] && [ "${index}" -lt 8 ]; do
    if [ "${url_values[${index}]}" != "${expected}" ]; then
      reported="${url_values[${index}]}"
      break
    fi
    index=$((index + 1))
  done
  if [ "${reported}" = "${expected}" ]; then
    reported="${url_values[1]}"
  fi
  printf '%s\n' "FAIL ${nupkg}: ${pkg}.nuspec expected licenseUrl ${expected} or none, found ${reported}" >&2
  return 1
}

# Requires the authors element to name Artur Bobb.
# The author is part of the license record a consumer sees next to NOTICE.
# Args: nupkg path, package id. Returns 1 when the exact element is absent.
# Reads ${work}/nuspec.
require_authors_element() {
  local nupkg="$1"
  local pkg="$2"
  if [ -z "${nupkg}" ] || [ -z "${pkg}" ] || [ ! -f "${work}/nuspec" ]; then
    echo "FAIL ${nupkg}: ${pkg}.nuspec expected <authors>Artur Bobb</authors>" >&2
    return 1
  fi
  if grep -F -q -- '<authors>Artur Bobb</authors>' "${work}/nuspec"; then
    return 0
  fi
  printf '%s\n' "FAIL ${nupkg}: ${pkg}.nuspec expected <authors>Artur Bobb</authors>" >&2
  return 1
}

# Checks one nupkg for NOTICE, one Apache-2.0 license, licenseUrl, authors,
# and copyright.
# Args: nupkg path. Returns 1 on the first miss. A pass prints nothing.
check_one_nupkg() {
  local nupkg="$1"
  local entry
  local pkg
  require_nupkg_file "${nupkg}"
  require_notice_bytes "${nupkg}"
  entry=$(extract_root_nuspec "${nupkg}")
  pkg=$(read_package_id "${nupkg}") || return 1
  require_one_license_element "${nupkg}" "${pkg}"
  require_nuspec_element "${nupkg}" "${entry}" '<license type="expression">Apache-2.0</license>'
  require_apache_license_url "${nupkg}" "${pkg}"
  require_authors_element "${nupkg}" "${pkg}"
  require_nuspec_element "${nupkg}" "${entry}" '<copyright>Copyright 2026 Artur Bobb</copyright>'
  note_package_id "${nupkg}"
}

# Confirms the arguments included both shipped packages.
# Checking one package and skipping the other would hide a bad nupkg.
# Args: none. Returns 1 when either id was not seen. Reads seen_alog and seen_ext.
require_both_packages() {
  if [ "${seen_alog}" -eq 1 ] && [ "${seen_ext}" -eq 1 ]; then
    return 0
  fi
  echo "FAIL expected both ALog and ALog.Extensions.Logging nupkgs (ALog=${seen_alog}, Extensions=${seen_ext})" >&2
  return 1
}

resolve_seen=0
resolve_paths=()

# Records one file hit while choosing the nupkg for a version-independent glob.
# The failure later reports the real match count, so this does not stop at the
# first extra file. Only the first 8 paths are kept for the printed list,
# which is the same bound as the direct nupkg arguments. The count itself
# stops at 200 so a huge directory cannot loop without a limit. A .snupkg is
# not a package consumers install. Args: path, pattern label, directory.
# Returns 1 on a symbol package or when the 200-path bound is hit. Updates
# resolve_seen and resolve_paths.
note_nupkg_match() {
  local path="$1"
  local label="$2"
  local dir="$3"
  if [ -z "${path}" ] || [ -z "${label}" ] || [ -z "${dir}" ]; then
    echo "FAIL nupkg match needs a path, a pattern, and a directory" >&2
    return 1
  fi
  case "${path}" in
    *.snupkg)
      echo "FAIL pattern ${label} in ${dir} matched a symbol package: ${path}" >&2
      return 1
      ;;
  esac
  if [ "${resolve_seen}" -ge 200 ]; then
    echo "FAIL ${dir}: expected exactly one ${label}, stopped at 200 matches" >&2
    return 1
  fi
  if [ "${resolve_seen}" -lt 8 ]; then
    resolve_paths[${resolve_seen}]="${path}"
  fi
  resolve_seen=$((resolve_seen + 1))
  return 0
}

# Prints the paths recorded for a glob, at most eight.
# The failure text has to name the matches so a duplicate package version is
# visible in CI. The message count can be higher than this list because only
# eight paths are stored. Args: none. Returns 0. Reads resolve_seen and
# resolve_paths.
print_resolved_matches() {
  local index=0
  while [ "${index}" -lt "${resolve_seen}" ] && [ "${index}" -lt 8 ]; do
    printf '%s\n' "${resolve_paths[${index}]}" >&2
    index=$((index + 1))
  done
  return 0
}

# Selects exactly one nupkg for the core package or the Extensions package.
# ALog.[0-9]*.nupkg requires a digit after "ALog.", so it does not match
# ALog.Extensions.Logging.*.nupkg. Both patterns end in .nupkg, so a .snupkg
# is not a match. Zero files or several files means this directory is not the
# single pack CI just produced. The number in the failure is the real glob
# count, not a stop at 8. The printed list still keeps only the first 8
# paths, and note_nupkg_match refuses a 201st path so the count stays bounded.
# Args: directory, pattern label, kind (core or ext). Prints the one path.
# Returns 1 when the count is not exactly one.
resolve_one_nupkg() {
  local dir="$1"
  local label="$2"
  local kind="$3"
  local path
  local had_nullglob=0
  local status=0
  if [ -z "${dir}" ] || [ ! -d "${dir}" ] || [ -z "${label}" ] || [ -z "${kind}" ]; then
    echo "FAIL nupkg resolve needs a directory, a pattern, and a kind" >&2
    return 1
  fi
  if [ "${kind}" != "core" ] && [ "${kind}" != "ext" ]; then
    echo "FAIL nupkg resolve kind must be core or ext, found ${kind}" >&2
    return 1
  fi
  resolve_seen=0
  resolve_paths=()
  if shopt -q nullglob; then
    had_nullglob=1
  fi
  shopt -s nullglob
  if [ "${kind}" = "core" ]; then
    for path in "${dir}"/ALog.[0-9]*.nupkg; do
      note_nupkg_match "${path}" "${label}" "${dir}" || status=1
      if [ "${status}" -ne 0 ]; then
        break
      fi
    done
  else
    for path in "${dir}"/ALog.Extensions.Logging.[0-9]*.nupkg; do
      note_nupkg_match "${path}" "${label}" "${dir}" || status=1
      if [ "${status}" -ne 0 ]; then
        break
      fi
    done
  fi
  if [ "${had_nullglob}" -eq 0 ]; then
    shopt -u nullglob
  fi
  if [ "${status}" -ne 0 ]; then
    print_resolved_matches
    return 1
  fi
  if [ "${resolve_seen}" -eq 0 ]; then
    printf '%s\n' "FAIL ${dir}: expected exactly one ${label}, found none. Pack that project into ${dir}." >&2
    return 1
  fi
  if [ "${resolve_seen}" -ne 1 ]; then
    printf '%s\n' "FAIL ${dir}: expected exactly one ${label}, found ${resolve_seen}. Empty ${dir} before packing." >&2
    print_resolved_matches
    return 1
  fi
  printf '%s\n' "${resolve_paths[0]}"
  return 0
}

# Rejects a --dir path that is missing or not a directory.
# A regular file would otherwise look like an empty pack and report "found
# none", which hides that the path itself is wrong. This runs before either
# glob. Args: directory path. Returns 1 when the path is not a directory.
require_pack_dir() {
  local dir="$1"
  if [ -z "${dir}" ] || [ ! -d "${dir}" ]; then
    printf '%s\n' "FAIL --dir ${dir}: directory not found." >&2
    return 1
  fi
  return 0
}

if [ "${1:-}" = "--dir" ]; then
  if [ "$#" -ne 2 ]; then
    fail_usage
    exit 1
  fi
  pack_dir="$2"
  require_pack_dir "${pack_dir}" || exit 1
  core_nupkg=$(resolve_one_nupkg "${pack_dir}" 'ALog.[0-9]*.nupkg' core) || exit 1
  ext_nupkg=$(resolve_one_nupkg "${pack_dir}" 'ALog.Extensions.Logging.[0-9]*.nupkg' ext) || exit 1
  set -- "${core_nupkg}" "${ext_nupkg}"
fi

require_nupkg_count "$#"
work=$(mktemp -d)
count="$#"
index=0
while [ "${index}" -lt "${count}" ]; do
  if [ "${index}" -ge 8 ]; then
    echo "FAIL nupkg license check stopped at its 8 package bound" >&2
    exit 1
  fi
  index=$((index + 1))
  check_one_nupkg "$1"
  shift
done
require_both_packages
echo "nupkg license checks passed"
