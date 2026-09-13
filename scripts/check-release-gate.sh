#!/usr/bin/env bash
# check-release-gate.sh — fail when the release path's approval gate is inert.
#
#   scripts/check-release-gate.sh [workflows-dir]     (default: .github/workflows)
#
# WHY THIS EXISTS (gh#1187)
# `environment: production` does not create or require anything. If the environment does not
# exist, GitHub CREATES IT AT RUN TIME WITH NO PROTECTION RULES and the job passes straight
# through. That is a repository SETTING, not YAML. Reading release.yml proves only that the
# workflow ASKS for a gate. Shape from TopstepX check-release-gate.sh (their gh#108).
#
# WHAT IT ASSERTS, for every environment any workflow in this repo names:
#   1. the environment exists — a 404 means the next run auto-creates it, unprotected;
#   2. it carries a required_reviewers protection rule;
#   3. that rule names at least one reviewer.
# Discovery of no environment: key is a FAILURE (the gate was deleted, or this script no
# longer matches how the workflows are written). A ${{ }} name is UNCHECKABLE.
#
# deploy.yml names a workflow_dispatch *input* `environment` — discovery starts at `jobs:`
# so that input is not reported as a gate.

set -euo pipefail

die() { printf '\033[31m%s\033[0m\n' "$*" >&2; exit 1; }
info() { printf '%s\n' "$*"; }
ok() { printf '\033[32m%s\033[0m\n' "$*"; }

WORKFLOWS_DIR="${1:-.github/workflows}"

# Discovery is a file read. Auth is required only for the API half below — a fixture
# self-test that must go red on a missing key must not fail first as "gh is not
# authenticated" (that exit is also what a runner without a token produces).
[ -d "$WORKFLOWS_DIR" ] || die "no such directory: $WORKFLOWS_DIR"

workflow_files=()
for candidate in "$WORKFLOWS_DIR"/*.yml "$WORKFLOWS_DIR"/*.yaml; do
  [ -f "$candidate" ] || continue
  workflow_files+=("$candidate")
done
[ "${#workflow_files[@]}" -gt 0 ] || die "no workflow files (*.yml, *.yaml) under $WORKFLOWS_DIR.
There is nothing here that could declare a release gate, so this cannot report one sound."

discovered="$(
  awk '
    function indent_of(s,   t) { t = s; sub(/[^ \t].*$/, "", t); return length(t) }
    function close_pending() { print pendingfile "\t<unresolved-mapping>"; pending = 0 }

    FNR == 1 { if (pending) close_pending(); injobs = 0 }
    /^[[:space:]]*#/ { next }
    $0 == "jobs:" { injobs = 1; next }
    !injobs { next }
    match($0, /^    environment:[[:space:]]*/) {
      if (pending) close_pending()
      rest = substr($0, RSTART + RLENGTH)
      sub(/[[:space:]]*#.*$/, "", rest)
      gsub(/^["'"'"']|["'"'"']$/, "", rest)
      if (rest != "") { print FILENAME "\t" rest; next }
      pending = 1
      pendingindent = indent_of($0)
      pendingfile = FILENAME
      next
    }
    pending && match($0, /^[[:space:]]*name:[[:space:]]*/) && indent_of($0) > pendingindent {
      rest = substr($0, RSTART + RLENGTH)
      sub(/[[:space:]]*#.*$/, "", rest)
      gsub(/^["'"'"']|["'"'"']$/, "", rest)
      print pendingfile "\t" rest
      pending = 0
      next
    }
    pending && $0 ~ /[^[:space:]]/ && indent_of($0) <= pendingindent { close_pending() }
    END { if (pending) close_pending() }
  ' "${workflow_files[@]}" | sort -u
)"

if [ -z "$discovered" ]; then
  die "no \`environment:\` key found in any workflow under $WORKFLOWS_DIR.

That is a FAILURE, not a clean run. The release path's approval gate IS an \`environment:\` key, so
finding none means either the gate has been deleted or this script's discovery no longer matches
how the workflows are written. Both leave the publish path ungated."
fi

info "Environments named by workflows under $WORKFLOWS_DIR:"
printf '%s\n' "$discovered" | while IFS="$(printf '\t')" read -r file name; do
  info "  $name    ($file)"
done
info ""

# File-level faults do not need the API (and must not hide behind "gh is not authenticated").
uncheckable=0
while IFS= read -r name; do
  [ -n "$name" ] || continue
  case "$name" in
    '<unresolved-mapping>')
      printf '\033[31mUNCHECKABLE\033[0m  an `environment:` mapping with no `name:` under it\n' >&2
      uncheckable=$((uncheckable + 1))
      ;;
    *'${{'*)
      printf '\033[31mUNCHECKABLE\033[0m  %s\n' "$name" >&2
      printf '  The environment name is not a literal, so no API call can confirm what it resolves to.\n' >&2
      uncheckable=$((uncheckable + 1))
      ;;
  esac
done <<PRE
$(printf '%s\n' "$discovered" | cut -f2- | sort -u)
PRE
[ "$uncheckable" -eq 0 ] || die "$uncheckable environment name(s) cannot be vouched for from the file."

command -v gh >/dev/null 2>&1 || die "gh is required (https://cli.github.com)"
# GH_TOKEN is how Actions authenticates; `gh auth status` is the local login.
if [ -z "${GH_TOKEN:-${GITHUB_TOKEN:-}}" ]; then
  gh auth status >/dev/null 2>&1 || die "gh is not authenticated. Run: gh auth login"
fi

REPO="${GITHUB_REPOSITORY:-}"
if [ -z "$REPO" ]; then
  REPO="$(gh repo view --json nameWithOwner --jq .nameWithOwner)" \
    || die "could not determine the repository. Set GITHUB_REPOSITORY=<owner/repo>."
fi

ENV_FIELDS='"\([.protection_rules[]?|select(.type=="required_reviewers")|.reviewers[]?|"\(.type):\(.reviewer.login // .reviewer.slug // "?")"]|join(", "))\t\([.protection_rules[]?|select(.type!="required_reviewers")|.type]|join(", "))\t\([.protection_rules[]?|select(.type=="required_reviewers")|.prevent_self_review][0] // false)\t\(.can_admins_bypass // false)"'

names="$(printf '%s\n' "$discovered" | cut -f2- | sort -u)"
TAB="$(printf '\t')"
failed=0
checked=0

while IFS= read -r name; do
  [ -n "$name" ] || continue
  checked=$((checked + 1))

  case "$name" in
    '<unresolved-mapping>')
      printf '\033[31mUNCHECKABLE\033[0m  an `environment:` mapping with no `name:` under it\n' >&2
      failed=$((failed + 1))
      continue
      ;;
    *'${{'*)
      printf '\033[31mUNCHECKABLE\033[0m  %s\n' "$name" >&2
      printf '  The environment name is not a literal, so no API call can confirm what it resolves to.\n' >&2
      failed=$((failed + 1))
      continue
      ;;
  esac

  if ! line="$(gh api "repos/$REPO/environments/$name" --jq "$ENV_FIELDS" 2>&1)"; then
    printf '\033[31mMISSING OR UNREADABLE\033[0m  %s\n' "$name" >&2
    printf '%s\n' "$line" | sed 's/^/  | /' >&2
    if printf '%s' "$line" | grep -q 'HTTP 404'; then
      printf '  The environment "%s" does not exist in %s.\n' "$name" "$REPO" >&2
      printf '  Create it with a required reviewer — scripts/bootstrap.sh %s does exactly that.\n' "$REPO" >&2
    fi
    failed=$((failed + 1))
    continue
  fi

  [ -n "$line" ] || die "read \"$name\" successfully but extracted nothing from it."

  reviewers="${line%%${TAB}*}"
  rest="${line#*${TAB}}"
  other_rules="${rest%%${TAB}*}"
  rest="${rest#*${TAB}}"
  self_review="${rest%%${TAB}*}"
  admins_bypass="${rest#*${TAB}}"

  if [ -z "$reviewers" ]; then
    printf '\033[31mUNPROTECTED\033[0m  %s\n' "$name" >&2
    if [ -n "$other_rules" ]; then
      printf '  It exists and carries: %s — but no required_reviewers rule naming anyone.\n' "$other_rules" >&2
    else
      printf '  It exists and carries NO protection rules at all.\n' >&2
    fi
    printf '  Fix: add a required reviewer. scripts/bootstrap.sh %s creates it that way.\n' "$REPO" >&2
    failed=$((failed + 1))
    continue
  fi

  ok "PROTECTED    $name"
  info "  required reviewers : $reviewers"
  if [ -n "$other_rules" ]; then
    info "  other rules        : $other_rules"
  fi
  info "  prevent_self_review: $self_review    can_admins_bypass: $admins_bypass   (recorded, not asserted)"
done <<NAMES
$names
NAMES

info ""
if [ "$failed" -gt 0 ]; then
  die "$failed of $checked environment(s) would not stop an unattended publish.

The approval gate is the only thing between a release and a public GHCR version tag, and a published
image cannot be un-pulled. Fix the environment; do not delete the \`environment:\` key to make this green."
fi

ok "ok  $checked environment(s) checked in $REPO; each exists and requires a named reviewer."
