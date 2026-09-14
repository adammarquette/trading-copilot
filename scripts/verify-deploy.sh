#!/usr/bin/env bash
# Verifies that a deployed Trading Co-Pilot instance is actually serving (gh#379, pipeline step 6).
#
# Probes /ready, not /health. /health answers from the process and proves only that something is listening --
# it returns 200 from an instance whose database is unreachable, which is exactly the failure a post-deploy
# check exists to catch. /ready runs CanConnectAsync and answers 503 when the app cannot reach Postgres.
#
# GET-only and read-only by construction: this runs against a deployed environment, and on the production
# path that environment is a live, real-money account. Nothing here may write.
#
# Usage:  scripts/verify-deploy.sh <base-url> [attempts] [delay-seconds]
# Local:  scripts/verify-deploy.sh http://localhost:8080
set -euo pipefail

BASE_URL="${1:-}"
ATTEMPTS="${2:-20}"
DELAY="${3:-15}"

if [ -z "$BASE_URL" ]; then
  echo "::error::verify-deploy.sh needs a base URL (e.g. https://trading-copilot-staging.up.railway.app)."
  exit 2
fi

# Trailing slashes would produce '//ready', which some proxies redirect and others 404.
BASE_URL="${BASE_URL%/}"
READY_URL="$BASE_URL/ready"

echo "Verifying $READY_URL (up to $ATTEMPTS attempts, ${DELAY}s apart)."

# A deploy is not instant: the container has to start, apply migrations and open a listener. Retrying is the
# difference between verifying the deploy and racing it -- a single immediate probe reports a healthy release
# as broken, which trains everyone to ignore this gate.
attempt=1
while [ "$attempt" -le "$ATTEMPTS" ]; do
  # curl already emits 000 via --write-out when it cannot connect, so the fallback replaces the value rather
  # than appending to it -- `|| echo 000` concatenates into "000000" and makes the failure unreadable.
  status=$(curl --silent --output /dev/null --write-out '%{http_code}' \
             --max-time 10 --request GET "$READY_URL" 2>/dev/null) || status="000"

  if [ "$status" = "200" ]; then
    echo "Ready after $attempt attempt(s)."
    exit 0
  fi

  echo "  attempt $attempt/$ATTEMPTS: HTTP $status"
  attempt=$((attempt + 1))
  [ "$attempt" -le "$ATTEMPTS" ] && sleep "$DELAY"
done

echo "::error::$READY_URL never returned 200 (last status: ${status:-none})."
echo "::error::Treat this release as suspect: flag it for rollback and pin the previous :sha-<short> tag."
exit 1
