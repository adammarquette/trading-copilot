#!/usr/bin/env bash
# deploy-environment-selftest.sh — require that deploy-environment.sh can still go red, and that a
# sound run never writes SSM, never references :latest, and passes digest+version as parameters.
#
#   scripts/deploy-environment-selftest.sh
#
# Exit status alone is also what "aws is required" produces. Each red case matches on the words
# that name ITS OWN fault (gh#1187).

set -euo pipefail

red() { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok() { printf '\033[32m%s\033[0m\n' "$*"; }
info() { printf '%s\n' "$*"; }
die() { red "$*"; exit 1; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="$REPO_ROOT/scripts/deploy-environment.sh"
[ -f "$SCRIPT" ] || die "not found: $SCRIPT"

FIXTURES="$(mktemp -d)"
trap 'rm -rf "$FIXTURES"' EXIT

SOUND_DIGEST="sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
failures=0

write_fakes() {
  local dir="$1"
  mkdir -p "$dir"
  : > "$dir/aws.log"
  : > "$dir/cdk.log"

  cat > "$dir/aws" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
log="${DEPLOY_ENVIRONMENT_FAKE_DIR}/aws.log"
printf '%s\n' "$*" >> "$log"
case "$1 $2" in
  "configure get")
    printf '%s\n' "${DEPLOY_ENVIRONMENT_FAKE_REGION:-eu-west-1}"
    ;;
  "sts get-caller-identity")
    printf '%s\n' "999999999999"
    ;;
  *)
    printf 'unexpected aws: %s\n' "$*" >&2
    exit 2
    ;;
esac
FAKE

  cat > "$dir/cdk" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
log="${DEPLOY_ENVIRONMENT_FAKE_DIR}/cdk.log"
printf '%s\n' "$*" >> "$log"
FAKE

  chmod +x "$dir/aws" "$dir/cdk"
}

expect_red() {
  local label="$1" needle="$2"
  shift 2
  local out status=0
  out="$(DEPLOY_ENVIRONMENT_FAKE_DIR="$FIXTURES" \
    DEPLOY_ENVIRONMENT_AWS="$FIXTURES/aws" \
    DEPLOY_ENVIRONMENT_CDK="$FIXTURES/cdk" \
    DEPLOY_OUTBOUND="${DEPLOY_OUTBOUND:-NatGateway}" \
    bash "$SCRIPT" "$@" 2>&1)" || status=$?
  if [ "$status" -eq 0 ]; then
    red "SELF-TEST FAILED  $label"
    info "  The script accepted a case it must reject."
    printf '%s\n' "$out" | sed 's/^/  | /'
    failures=$((failures + 1))
    return
  fi
  case "$out" in
    *"$needle"*) ok "rejected  $label" ;;
    *)
      red "SELF-TEST FAILED  $label"
      info "  exited $status but never said: $needle"
      printf '%s\n' "$out" | sed 's/^/  | /'
      failures=$((failures + 1))
      ;;
  esac
}

write_fakes "$FIXTURES"

expect_red "empty version" "version is empty" staging "" "$SOUND_DIGEST"
expect_red "latest version" ":latest" staging latest "$SOUND_DIGEST"
expect_red "latest digest" ":latest" staging 1.2.3 "sha256:latest"
expect_red "short digest" "sha256:" staging 1.2.3 "sha256:abcd"
expect_red "wrong env" "staging or production" sandbox 1.2.3 "$SOUND_DIGEST"

out="$(DEPLOY_OUTBOUND= bash "$SCRIPT" staging 1.2.3 "$SOUND_DIGEST" 2>&1)" || status=$?
if [ "${status:-0}" -eq 0 ] || ! printf '%s' "$out" | grep -q "DEPLOY_OUTBOUND"; then
  red "SELF-TEST FAILED  missing outbound"
  printf '%s\n' "$out" | sed 's/^/  | /'
  failures=$((failures + 1))
else
  ok "rejected  missing outbound"
fi
status=0

out="$(DEPLOY_OUTBOUND=NatGateway DEPLOY_ROOT_DOMAIN= bash "$SCRIPT" staging 1.2.3 "$SOUND_DIGEST" 2>&1)" || status=$?
if [ "${status:-0}" -eq 0 ] || ! printf '%s' "$out" | grep -q "DEPLOY_ROOT_DOMAIN"; then
  red "SELF-TEST FAILED  missing root domain"
  printf '%s\n' "$out" | sed 's/^/  | /'
  failures=$((failures + 1))
else
  ok "rejected  missing root domain"
fi
status=0

out="$(DEPLOY_ENVIRONMENT_FAKE_DIR="$FIXTURES" \
  DEPLOY_ENVIRONMENT_AWS="$FIXTURES/aws" \
  DEPLOY_ENVIRONMENT_CDK="$FIXTURES/cdk" \
  DEPLOY_OUTBOUND=NatGateway \
  DEPLOY_ROOT_DOMAIN=staging.marqspec.com \
  bash "$SCRIPT" production 1.2.3 "$SOUND_DIGEST" 2>&1)" || status=$?
if [ "${status:-0}" -eq 0 ] || ! printf '%s' "$out" | grep -q "second staging.marqspec.com"; then
  red "SELF-TEST FAILED  production reused staging root domain"
  printf '%s\n' "$out" | sed 's/^/  | /'
  failures=$((failures + 1))
else
  ok "rejected  production reused staging root domain"
fi
status=0

write_fakes "$FIXTURES"
if ! out="$(DEPLOY_ENVIRONMENT_FAKE_DIR="$FIXTURES" \
  DEPLOY_ENVIRONMENT_AWS="$FIXTURES/aws" \
  DEPLOY_ENVIRONMENT_CDK="$FIXTURES/cdk" \
  DEPLOY_OUTBOUND="NatGateway" \
  DEPLOY_ROOT_DOMAIN="staging.marqspec.com" \
  bash "$SCRIPT" staging 1.2.3 "$SOUND_DIGEST" 2>&1)"; then
  red "SELF-TEST FAILED  sound staging run"
  printf '%s\n' "$out" | sed 's/^/  | /'
  failures=$((failures + 1))
else
  cdk_log="$(cat "$FIXTURES/cdk.log")"
  missing=""
  printf '%s' "$cdk_log" | grep -Fq "trading-copilot-staging" || missing="${missing} stack"
  printf '%s' "$cdk_log" | grep -Fq "ImageDigest=${SOUND_DIGEST}" || missing="${missing} digest"
  printf '%s' "$cdk_log" | grep -Fq "Version=1.2.3" || missing="${missing} version"
  printf '%s' "$cdk_log" | grep -Fq "outbound=NatGateway" || missing="${missing} outbound"
  printf '%s' "$cdk_log" | grep -Fq "account=999999999999" || missing="${missing} account"
  printf '%s' "$cdk_log" | grep -Fq "region=eu-west-1" || missing="${missing} region"
  printf '%s' "$cdk_log" | grep -Fq "rootDomain=staging.marqspec.com" || missing="${missing} rootDomain"
  if printf '%s' "$cdk_log" | grep -Fq -- "--no-lookups"; then
    red "SELF-TEST FAILED  sound staging run passed --no-lookups (staging apply must Lookup the existing zone)"
    printf '%s\n' "$cdk_log" | sed 's/^/  | /'
    failures=$((failures + 1))
  fi
  if [ -n "$missing" ]; then
    red "SELF-TEST FAILED  sound staging run — cdk log missing:$missing"
    printf '%s\n' "$cdk_log" | sed 's/^/  | /'
    failures=$((failures + 1))
  else
    ok "sound staging run passes digest+version as parameters"
  fi
  if printf '%s' "$cdk_log" | grep -Eq 'put-parameter|resolve:ssm|:latest'; then
    red "SELF-TEST FAILED  sound run mentioned put-parameter, resolve:ssm, or :latest"
    failures=$((failures + 1))
  fi
fi

info ""
if [ "$failures" -gt 0 ]; then
  die "$failures deploy-environment self-test assertion(s) failed."
fi
ok "ok  deploy-environment.sh still rejects a bad deploy and a sound one never writes SSM."
