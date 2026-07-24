#!/usr/bin/env bash
#
# reviewer-review.sh — render a PR review AS the reviewer GitHub App
# (trading-copilot-reviewer[bot]), so an agent that authenticates as the
# maintainer can still submit a formal Approve / Request-changes verdict on a
# maintainer-authored PR. GitHub blocks self-review; a distinct App identity is
# not the author, so the verdict is accepted (gh#141).
#
# Substance: documentation/agents/code-reviewer.md · setup: deployment runbook.
#
# Requires in the environment (NEVER in source — see the runbook):
#   REVIEWER_APP_ID                 the App ID (a number)
#   REVIEWER_APP_INSTALLATION_ID    the installation ID (a number)
#   and ONE of:
#   REVIEWER_APP_PRIVATE_KEY_FILE   path to the unmodified .pem  (RECOMMENDED —
#                                   no escaping/quoting to get wrong in .env)
#   REVIEWER_APP_PRIVATE_KEY        the .pem contents inline (multi-line or \n-escaped)
# Optional:
#   REVIEWER_REPO                   default: adammarquette/trading-copilot
#
# Usage:
#   reviewer-review.sh verify
#       Mint a token and show the installation — proves the identity works,
#       posts nothing.
#   reviewer-review.sh review <pr> <APPROVE|REQUEST_CHANGES|COMMENT> <body-file>
#       Submit the verdict as the bot. Prints the review id, the bot login it
#       posted as, and the state — the proof that self-review was bypassed.
#
# With REVIEWER_APP_PRIVATE_KEY_FILE, openssl reads the .pem directly. With the
# inline REVIEWER_APP_PRIVATE_KEY, the key is written to a private (0600) temp
# file for the openssl call and removed immediately (process substitution is NOT
# portable to native-Windows openssl). The key is never printed, and the minted
# token is only ever passed to gh via GH_TOKEN, never echoed.

set -euo pipefail

REPO="${REVIEWER_REPO:-adammarquette/trading-copilot}"

die() { printf 'reviewer-review: %s\n' "$*" >&2; exit 1; }

# gh api with MSYS path conversion disabled, so Git Bash does not rewrite a
# /app/... endpoint into a Windows path before gh sees it. Scoped to gh only
# (NOT exported): openssl below needs conversion left ON so its temp-file path
# stays Windows-resolvable. No-op off Windows.
ghapi() { MSYS_NO_PATHCONV=1 gh api "$@"; }

: "${REVIEWER_APP_ID:?REVIEWER_APP_ID is not set (see the deployment runbook)}"
: "${REVIEWER_APP_INSTALLATION_ID:?REVIEWER_APP_INSTALLATION_ID is not set}"
if [ -z "${REVIEWER_APP_PRIVATE_KEY_FILE:-}" ] && [ -z "${REVIEWER_APP_PRIVATE_KEY:-}" ]; then
  die "set REVIEWER_APP_PRIVATE_KEY_FILE (path to the unmodified .pem — recommended) or REVIEWER_APP_PRIVATE_KEY (inline PEM)"
fi

command -v openssl >/dev/null || die "openssl not found"
command -v gh       >/dev/null || die "gh not found"

b64url() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }

mint_token() {
  local keyfile tmpkey="" now header payload unsigned sig jwt
  if [ -n "${REVIEWER_APP_PRIVATE_KEY_FILE:-}" ]; then
    # Preferred: point at the unmodified .pem GitHub downloaded — no \n-escaping,
    # quoting, or single-lining to get wrong. openssl reads it directly.
    [ -r "$REVIEWER_APP_PRIVATE_KEY_FILE" ] || die "REVIEWER_APP_PRIVATE_KEY_FILE is not readable: $REVIEWER_APP_PRIVATE_KEY_FILE"
    keyfile="$REVIEWER_APP_PRIVATE_KEY_FILE"
  else
    # Inline PEM: normalise \n escapes and write a 0600 temp file (openssl needs a
    # file; process substitution is not portable to native-Windows openssl).
    local key="${REVIEWER_APP_PRIVATE_KEY//\\n/$'\n'}"
    tmpkey=$(mktemp) || die "mktemp failed"
    chmod 600 "$tmpkey" 2>/dev/null || true
    trap 'rm -f "$tmpkey"' EXIT
    printf '%s\n' "$key" > "$tmpkey"
    keyfile="$tmpkey"
  fi
  now=$(date +%s)
  header=$(printf '%s' '{"alg":"RS256","typ":"JWT"}' | b64url)
  # iat backdated 60s for clock skew; exp 9 min out (GitHub caps JWT life at 10).
  payload=$(printf '{"iat":%d,"exp":%d,"iss":"%s"}' "$((now - 60))" "$((now + 540))" "$REVIEWER_APP_ID" | b64url)
  unsigned="${header}.${payload}"
  sig=$(printf '%s' "$unsigned" | openssl dgst -sha256 -sign "$keyfile" -binary | b64url) \
    || die "JWT signing failed — is the private key a valid PEM?"
  [ -n "$tmpkey" ] && { rm -f "$tmpkey"; trap - EXIT; }
  jwt="${unsigned}.${sig}"
  ghapi -H "Authorization: Bearer ${jwt}" -X POST \
    "/app/installations/${REVIEWER_APP_INSTALLATION_ID}/access_tokens" --jq '.token' \
    || die "installation-token exchange failed — check REVIEWER_APP_ID / REVIEWER_APP_INSTALLATION_ID and that the App is installed on ${REPO}"
}

cmd="${1:-}"
case "$cmd" in
  verify)
    token=$(mint_token)
    GH_TOKEN="$token" ghapi /installation/repositories --jq \
      '"OK — token minted; installation can reach " + (.total_count|tostring) + " repo(s): " + ([.repositories[].full_name] | join(", "))'
    ;;
  review)
    pr="${2:?usage: review <pr> <APPROVE|REQUEST_CHANGES|COMMENT> <body-file>}"
    event="${3:?missing state: APPROVE | REQUEST_CHANGES | COMMENT}"
    body_file="${4:?missing <body-file>}"
    case "$event" in APPROVE|REQUEST_CHANGES|COMMENT) ;; *) die "state must be APPROVE, REQUEST_CHANGES, or COMMENT (got '$event')";; esac
    [ -f "$body_file" ] || die "body file not found: $body_file"
    body=$(cat "$body_file")
    token=$(mint_token)
    GH_TOKEN="$token" ghapi "/repos/${REPO}/pulls/${pr}/reviews" -X POST \
      -f event="$event" -f body="$body" --jq \
      '"review " + (.id|tostring) + " submitted as " + .user.login + " — state " + .state'
    ;;
  ""|-h|--help|help)
    grep '^#' "$0" | sed 's/^# \{0,1\}//'
    ;;
  *)
    die "unknown command '$cmd' — try: verify | review <pr> <STATE> <body-file>"
    ;;
esac
