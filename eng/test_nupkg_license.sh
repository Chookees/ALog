#!/usr/bin/env bash
# Checks packed ALog nupkgs, not symbol packages, against the repo license.
# Each package must carry the repository NOTICE and an Apache-2.0 nuspec
# whose copyright names Artur Bobb. A miss means the downloadable package
# would not match the license published in source.

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
    echo "FAIL usage: eng/test_nupkg_license.sh <ALog.nupkg> <ALog.Extensions.Logging.nupkg> (2 to 8 nupkgs, no snupkg)" >&2
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

# Records which shipped package id this nuspec belongs to.
# The check is not done until both ALog and ALog.Extensions.Logging have been
# seen. Args: nupkg path. Returns 1 when the id is neither of those two.
# Reads ${work}/nuspec. Sets seen_alog or seen_ext.
note_package_id() {
  local nupkg="$1"
  if [ -z "${nupkg}" ] || [ ! -f "${work}/nuspec" ]; then
    echo "FAIL ${nupkg}: expected a nuspec id of ALog or ALog.Extensions.Logging" >&2
    return 1
  fi
  if grep -F -q -- '<id>ALog.Extensions.Logging</id>' "${work}/nuspec"; then
    seen_ext=1
    return 0
  fi
  if grep -F -q -- '<id>ALog</id>' "${work}/nuspec"; then
    seen_alog=1
    return 0
  fi
  echo "FAIL ${nupkg}: nuspec id must be ALog or ALog.Extensions.Logging" >&2
  return 1
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

# Checks one nupkg for NOTICE, the Apache-2.0 license expression, and copyright.
# Args: nupkg path. Returns 1 on the first miss. A pass prints nothing.
check_one_nupkg() {
  local nupkg="$1"
  local entry
  require_nupkg_file "${nupkg}"
  require_notice_bytes "${nupkg}"
  entry=$(extract_root_nuspec "${nupkg}")
  require_nuspec_element "${nupkg}" "${entry}" '<license type="expression">Apache-2.0</license>'
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
