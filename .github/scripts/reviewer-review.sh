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
#   REVIEWER_APP_PRIVATE_KEY        the .pem contents (multi-line or \n-escaped)
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
# The private key is written only to a private (0600) temp file for the openssl
# call and removed immediately — process substitution is NOT portable to
# native-Windows openssl, which cannot read a Git Bash /proc/*/fd path. The key
# is never printed, and the minted token is only ever passed to gh via GH_TOKEN,
# never echoed.

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
: "${REVIEWER_APP_PRIVATE_KEY:?REVIEWER_APP_PRIVATE_KEY is not set}"

command -v openssl >/dev/null || die "openssl not found"
command -v gh       >/dev/null || die "gh not found"

b64url() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }

mint_token() {
  # Accept both storage styles: a real multi-line PEM, or one line with \n escapes.
  local key="${REVIEWER_APP_PRIVATE_KEY//\\n/$'\n'}"
  local keyfile now header payload unsigned sig jwt
  # openssl needs the key as a file. Process substitution is not portable to
  # native-Windows openssl, so use a 0600 temp file removed when this (sub)shell
  # exits — including on error via die.
  keyfile=$(mktemp) || die "mktemp failed"
  chmod 600 "$keyfile" 2>/dev/null || true
  trap 'rm -f "$keyfile"' EXIT
  printf '%s\n' "$key" > "$keyfile"
  now=$(date +%s)
  header=$(printf '%s' '{"alg":"RS256","typ":"JWT"}' | b64url)
  # iat backdated 60s for clock skew; exp 9 min out (GitHub caps JWT life at 10).
  payload=$(printf '{"iat":%d,"exp":%d,"iss":"%s"}' "$((now - 60))" "$((now + 540))" "$REVIEWER_APP_ID" | b64url)
  unsigned="${header}.${payload}"
  sig=$(printf '%s' "$unsigned" | openssl dgst -sha256 -sign "$keyfile" -binary | b64url) \
    || die "JWT signing failed — is REVIEWER_APP_PRIVATE_KEY a valid PEM?"
  rm -f "$keyfile"; trap - EXIT
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
