#!/usr/bin/env bash
# Starts the SQL Server container used by the ALog CI tests.
#
# services: containers start before any step, so they cannot use a password
# created in a step. This script generates the password, masks it, pulls the
# digest-pinned image, and waits until SQL Server is ready. The image argument
# is the single digest reference owned by the workflow. Next: SqlLiveIntegrationTests,
# which reads ALOG_SQL_TEST_CONNECTION when CI sets it.
#
# Tests source this file and replace sql_is_ready and pause_between_attempts.
# Sourcing does not start a container; only an executed script calls main.

set -euo pipefail
# A generated password contains '!'. History expansion would rewrite it.
set +H

# Fixed by the workflow contract: 60 tries, 2 seconds apart.
readonly SQL_MAX_ATTEMPTS=60
readonly SQL_CONTAINER_NAME="alog-sql-ci"

# Prints ::add-mask:: so later log lines that contain the password are hidden.
# Args: the password. Empty is rejected. Writes one line to stdout and nothing else.
# Returns 0, or 1 when the password is empty.
register_secret() {
  local secret="$1"
  if [ -z "$secret" ]; then
    echo "Refusing to mask an empty SQL password." >&2
    return 1
  fi
  printf '::add-mask::%s\n' "$secret"
}

# Builds a SQL Server password. The Aa1! prefix covers the four character
# classes SQL Server requires; the tail is 24 hex characters from openssl.
# Returns the password on stdout. Returns 1 when openssl output is the wrong shape.
# Does not log the password. Safe to call more than once; each call draws again.
create_sql_password() {
  local entropy
  if ! entropy=$(openssl rand -hex 12); then
    echo "openssl could not generate SQL password entropy." >&2
    return 1
  fi
  entropy=${entropy//$'\n'/}
  entropy=${entropy//$'\r'/}
  if ! printf '%s' "$entropy" | grep -Eq '^[0-9a-f]{24}$'; then
    echo "SQL password entropy was not 24 hex characters." >&2
    return 1
  fi
  printf 'Aa1!%s' "$entropy"
}

# Prints the digest separator. The pieces stay split so this file is not an image pin.
# Returns the separator on stdout. Always succeeds.
digest_marker() {
  printf '%s%s' '@' 'sha256:'
}

# Rejects an image that is not a name, the digest separator, and 64 hex characters.
# A tag, including one written beside a digest, is rejected so the pin cannot move.
# Args: the image reference. Returns 0 when it is a digest pin, 1 otherwise.
require_digest_image() {
  local image="$1"
  local marker
  marker=$(digest_marker)
  if [ -z "$image" ]; then
    echo "SQL image argument is empty." >&2
    return 1
  fi
  case "$image" in
    *"$marker"*)
      ;;
    *)
      echo "SQL image must be referenced by digest, not a moving tag." >&2
      return 1
      ;;
  esac
  local name="${image%%"$marker"*}"
  case "$name" in
    *:*)
      echo "SQL image must not include a tag. Pin the digest only." >&2
      return 1
      ;;
  esac
  local digest="${image#*"$marker"}"
  if ! printf '%s' "$digest" | grep -Eq '^[0-9a-fA-F]{64}$'; then
    echo "SQL image digest must be 64 hex characters." >&2
    return 1
  fi
}

# Args: attempt count. Must be an integer from 1 to 60 with no leading zero.
# Returns 0 when the value is in range, 1 otherwise.
require_attempt_count() {
  local attempts="$1"
  case "$attempts" in
    ''|*[!0-9]*)
      echo "SQL readiness attempts must be an integer from 1 to 60." >&2
      return 1
      ;;
  esac
  if [ "$attempts" -lt 1 ] || [ "$attempts" -gt 60 ]; then
    echo "SQL readiness attempts must be from 1 to 60, got ${attempts}." >&2
    return 1
  fi
}

# Sleeps two seconds between readiness probes.
# Tests replace this function so the bound can be checked without waiting.
# Returns the sleep status.
pause_between_attempts() {
  sleep 2
}

# Reports whether the SQL container is ready.
# Returns 0 when the ready message is in the last 400 log lines, 1 when the
# container is running but not ready, and 2 when it has exited or disappeared.
# The 400-line cap keeps each probe bounded; the ready line is at the end of startup.
sql_is_ready() {
  local state
  if ! state=$(docker inspect -f '{{.State.Status}}' "$SQL_CONTAINER_NAME" 2>/dev/null); then
    echo "SQL container is not inspectable." >&2
    return 2
  fi
  if [ "$state" != "running" ]; then
    echo "SQL container status is ${state}." >&2
    return 2
  fi
  local logs
  logs=$(docker logs --tail 400 "$SQL_CONTAINER_NAME" 2>&1 || true)
  if printf '%s\n' "$logs" | grep -q "SQL Server is now ready for client connections"; then
    return 0
  fi
  return 1
}

# Probes at most the given number of times. 60 is the contract ceiling.
# A dead container (probe status 2) stops the loop instead of using the remaining tries.
# Args: attempt count from 1 to 60. Returns 0 when ready, 1 otherwise.
wait_for_sql() {
  local max_attempts="$1"
  require_attempt_count "$max_attempts" || return 1
  local attempt=1
  while [ "$attempt" -le "$max_attempts" ]; do
    local ready_status=0
    sql_is_ready || ready_status=$?
    if [ "$ready_status" -eq 0 ]; then
      return 0
    fi
    if [ "$ready_status" -eq 2 ]; then
      return 1
    fi
    if [ "$attempt" -eq "$max_attempts" ]; then
      return 1
    fi
    pause_between_attempts
    attempt=$((attempt + 1))
  done
  return 1
}

# Returns 0 when the password contains a character that would split the connection string.
# The checked set is fixed: semicolon, equals, space, quotes, braces, dollar, backtick, backslash.
# '!' is allowed because the generator uses it as the required symbol. Args: the password.
password_has_separator() {
  local password="$1"
  # Fixed set. '!' is intentionally absent; the generator uses it as the symbol.
  local -a forbidden=(';' '=' ' ' "'" '"' '{' '}' '$' '`' '\' $'\n' $'\r')
  local index=0
  while [ "$index" -lt "${#forbidden[@]}" ]; do
    case "$password" in
      *"${forbidden[$index]}"*)
        return 0
        ;;
    esac
    index=$((index + 1))
  done
  return 1
}

# Writes ALOG_SQL_TEST_CONNECTION to GITHUB_ENV. The password is one field.
# Args: the password. Separator characters are rejected.
# Returns 1 when GITHUB_ENV is unset, the password is empty, or a separator is present.
publish_connection() {
  local password="$1"
  if [ -z "${GITHUB_ENV:-}" ]; then
    echo "GITHUB_ENV is not set, so the SQL connection string has nowhere to go." >&2
    return 1
  fi
  if [ -z "$password" ]; then
    echo "Refusing to publish an empty SQL password." >&2
    return 1
  fi
  if password_has_separator "$password"; then
    echo "SQL password contains a character the connection string cannot carry." >&2
    return 1
  fi
  printf 'ALOG_SQL_TEST_CONNECTION=Server=127.0.0.1,1433;User Id=sa;Password=%s;TrustServerCertificate=True;Encrypt=False;\n' \
    "$password" >> "$GITHUB_ENV"
}

# Removes a container left by a local rerun. A missing container is not an error.
# Returns 0. Docker failures are ignored because the following pull reports a dead daemon.
remove_container() {
  docker rm -f "$SQL_CONTAINER_NAME" >/dev/null 2>&1 || true
}

# Pulls the pinned image and starts it. The password is passed as an environment
# value and is not printed. Args: image, password. Returns docker's status.
run_container() {
  local image="$1"
  local password="$2"
  if [ -z "$password" ]; then
    echo "Refusing to start SQL with an empty password." >&2
    return 1
  fi
  docker pull "$image"
  docker run -d --name "$SQL_CONTAINER_NAME" \
    --publish 1433:1433 \
    --env ACCEPT_EULA=Y \
    --env MSSQL_SA_PASSWORD="$password" \
    "$image"
}

# Requires one digest image argument, masks a new password, and publishes the
# connection string after SQL Server is ready. Returns 1 when it does not become ready.
main() {
  if [ "$#" -ne 1 ]; then
    echo "Usage: start-sql.sh <image$(digest_marker)digest>" >&2
    return 1
  fi
  local image="$1"
  require_digest_image "$image"
  if ! command -v docker >/dev/null 2>&1; then
    echo "docker is not on PATH." >&2
    return 1
  fi
  if ! command -v openssl >/dev/null 2>&1; then
    echo "openssl is not on PATH." >&2
    return 1
  fi
  local password
  password=$(create_sql_password)
  register_secret "$password"
  remove_container
  run_container "$image" "$password"
  if ! wait_for_sql "$SQL_MAX_ATTEMPTS"; then
    echo "SQL Server was not ready after ${SQL_MAX_ATTEMPTS} attempts." >&2
    docker logs --tail 80 "$SQL_CONTAINER_NAME" >&2 || true
    return 1
  fi
  publish_connection "$password"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  main "$@"
fi
