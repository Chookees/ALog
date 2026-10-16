#!/usr/bin/env bash
# Tests the SQL startup bounds and the password rules without Docker.
# sql_is_ready and pause_between_attempts are replaced after sourcing.

set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
# shellcheck source=start-sql.sh
source "$here/start-sql.sh"

failures=0

pass() {
  printf 'ok %s\n' "$1"
}

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

reset_probe() {
  ready_calls=0
  pause_calls=0
  sql_is_ready() {
    ready_calls=$((ready_calls + 1))
    return 1
  }
  pause_between_attempts() {
    pause_calls=$((pause_calls + 1))
  }
}

expect_fail "empty image" require_digest_image ""
expect_fail "moving tag" require_digest_image "example.invalid/sql:moving"
digest=$(printf 'a%.0s' {1..64})
marker=$(digest_marker)
expect_ok "digest image" require_digest_image "example.invalid/sql${marker}${digest}"
short=$(printf 'a%.0s' {1..63})
expect_fail "short digest" require_digest_image "example.invalid/sql${marker}${short}"
expect_fail "tag beside digest" require_digest_image "example.invalid/sql:moving${marker}${digest}"

expect_fail "zero attempts" require_attempt_count 0
expect_fail "61 attempts" require_attempt_count 61
expect_fail "word attempts" require_attempt_count foo
expect_ok "60 attempts" require_attempt_count 60
expect_ok "one attempt" require_attempt_count 1

reset_probe
expect_fail "60 failures" wait_for_sql 60
if [ "$ready_calls" -ne 60 ] || [ "$pause_calls" -ne 59 ]; then
  echo "FAIL 60-failure bound calls=${ready_calls} pauses=${pause_calls}" >&2
  failures=$((failures + 1))
else
  pass "60-failure bound"
fi

reset_probe
sql_is_ready() {
  ready_calls=$((ready_calls + 1))
  if [ "$ready_calls" -eq 60 ]; then
    return 0
  fi
  return 1
}
expect_ok "success on attempt 60" wait_for_sql 60
if [ "$ready_calls" -ne 60 ] || [ "$pause_calls" -ne 59 ]; then
  echo "FAIL success-on-60 calls=${ready_calls} pauses=${pause_calls}" >&2
  failures=$((failures + 1))
else
  pass "success on attempt 60 probe count"
fi

reset_probe
sql_is_ready() {
  ready_calls=$((ready_calls + 1))
  return 0
}
expect_ok "success on first probe" wait_for_sql 60
if [ "$ready_calls" -ne 1 ] || [ "$pause_calls" -ne 0 ]; then
  echo "FAIL first-probe calls=${ready_calls} pauses=${pause_calls}" >&2
  failures=$((failures + 1))
else
  pass "first probe does not sleep"
fi

reset_probe
sql_is_ready() {
  ready_calls=$((ready_calls + 1))
  return 2
}
expect_fail "dead container" wait_for_sql 60
if [ "$ready_calls" -ne 1 ] || [ "$pause_calls" -ne 0 ]; then
  echo "FAIL dead-container calls=${ready_calls} pauses=${pause_calls}" >&2
  failures=$((failures + 1))
else
  pass "dead container stops"
fi

reset_probe
expect_fail "61 is rejected before probing" wait_for_sql 61
if [ "$ready_calls" -ne 0 ]; then
  echo "FAIL 61 probed ${ready_calls} times" >&2
  failures=$((failures + 1))
else
  pass "61 does not probe"
fi

password=$(create_sql_password)
other=$(create_sql_password)
if [ "${#password}" -ne 28 ] || [ "${#other}" -ne 28 ]; then
  echo "FAIL password length" >&2
  failures=$((failures + 1))
elif [ "$password" = "$other" ]; then
  echo "FAIL two passwords matched" >&2
  failures=$((failures + 1))
else
  tail=${password#Aa1!}
  if ! printf '%s' "$tail" | grep -Eq '^[0-9a-f]{24}$'; then
    echo "FAIL password tail shape" >&2
    failures=$((failures + 1))
  else
    pass "password shape and uniqueness"
  fi
fi

err_file=$(mktemp)
mask_line=$(register_secret "Aa1!notsecret" 2>"$err_file")
if [ "$mask_line" != "::add-mask::Aa1!notsecret" ] || [ -s "$err_file" ]; then
  echo "FAIL mask line" >&2
  failures=$((failures + 1))
else
  pass "mask line"
fi
expect_fail "empty mask" register_secret ""
rm -f "$err_file"

env_file=$(mktemp)
GITHUB_ENV="$env_file"
export GITHUB_ENV
expect_ok "publish password" publish_connection "Aa1!abcdef0123456789abcdef"
python3 - "$env_file" << 'PY'
import sys
text = open(sys.argv[1], encoding="utf-8").read().strip()
prefix = "ALOG_SQL_TEST_CONNECTION=Server=127.0.0.1,1433;User Id=sa;Password="
suffix = ";TrustServerCertificate=True;Encrypt=False;"
if not text.startswith(prefix) or not text.endswith(suffix):
    raise SystemExit("connection string shape")
password = text[len(prefix):-len(suffix)]
if password != "Aa1!abcdef0123456789abcdef":
    raise SystemExit("password field mismatch")
if text.count("\n") != 0:
    raise SystemExit("extra lines")
PY
pass "connection string field"
: > "$env_file"
expect_fail "separator password" publish_connection "Aa1!bad;tail"
if [ -s "$env_file" ]; then
  echo "FAIL separator password was published" >&2
  failures=$((failures + 1))
else
  pass "separator password not published"
fi
rm -f "$env_file"
unset GITHUB_ENV

if [ "$failures" -ne 0 ]; then
  echo "${failures} start-sql tests failed" >&2
  exit 1
fi
echo "start-sql tests passed"
