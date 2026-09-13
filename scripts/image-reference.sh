#!/usr/bin/env bash
# image-reference.sh — print the GHCR repository this project publishes to.
#
#   scripts/image-reference.sh [owner/repo]     default: $GITHUB_REPOSITORY, else the git remote
#
# An OCI repository name must be lowercase. `${{ github.repository }}` carries the repository's
# DISPLAY case. Both release.yml and deploy.yml ask this script so a mixed-case reference fails
# a pull request (or a local run) instead of a release. Pattern: TopstepX scripts/image-reference.sh
# (their gh#115); this product's image is ghcr.io/adammarquette/trading-copilot (ADR-0018).

set -euo pipefail

REPO="${1:-${GITHUB_REPOSITORY:-}}"

if [ -z "$REPO" ] && command -v git >/dev/null 2>&1; then
  ORIGIN="$(git config --get remote.origin.url 2>/dev/null || true)"
  case "$ORIGIN" in
    *github.com[:/]*)
      REPO="${ORIGIN#*github.com}"
      REPO="${REPO#[:/]}"
      REPO="${REPO%.git}"
      ;;
  esac
fi

if [ -z "$REPO" ]; then
  printf '\033[31m%s\033[0m\n' \
    "usage: scripts/image-reference.sh <owner/repo>   (or set GITHUB_REPOSITORY, or run inside the repo)" >&2
  exit 1
fi

case "$REPO" in
  */*/*|/*|*/) printf '\033[31m%s\033[0m\n' "not an owner/repo pair: $REPO" >&2; exit 1 ;;
  */*) : ;;
  *) printf '\033[31m%s\033[0m\n' "not an owner/repo pair: $REPO" >&2; exit 1 ;;
esac

printf 'ghcr.io/%s\n' "$(printf '%s' "$REPO" | tr '[:upper:]' '[:lower:]')"
