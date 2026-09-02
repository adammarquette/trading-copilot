#!/usr/bin/env bash
# Runs the Category=Staging execution gates (gh#269, gh#293, gh#1012) against a LOCALLY COMPOSED app instead of a
# deployed Railway environment (gh#1074).
#
# WHY THIS EXISTS
# ----------------
# There is no staging environment and none is planned right now -- development runs on Docker locally
# (documentation/deployment-runbook.md, Operator setup). The gates in .github/workflows/staging-gates.yml were
# designed around a DEPLOYED instance, so on that path alone they had never once produced evidence for gh#1012 --
# the question of whether ProjectX auto-reduces the resting bracket on a partial close was still unanswered, and
# PR #928/#1013 was held VERIFY-FIRST on evidence that was never going to arrive.
#
# The gates do not actually need Railway. StagingApiClient authenticates against a base URL and starts no host of
# its own; StagingProjectXGateway reads venue truth DIRECTLY from ProjectX, so the deployment's identity is
# irrelevant to the assertion. A `docker compose up -d` app on http://localhost:8080 satisfies the contract as
# well as a deployed one -- this script is that second, local path. It does not replace the deployed-staging path
# (.github/workflows/staging-gates.yml); it is a second way to satisfy the same contract.
#
# SAFETY -- READ THIS BEFORE RUNNING
# -----------------------------------
# THIS PLACES REAL ORDERS on a real ProjectX account. R-14 (practice accounts only outside production) must hold
# by construction: StagingProjectXGateway.ResolvePracticeAccountId (gh#1074) independently verifies the venue's
# own `Simulated` flag on the resolved account and REFUSES to hand back an id for any account it reports as
# non-simulated -- so even a misconfigured STAGING_PROJECTX_API_KEY/SECRET pointed at a live account cannot be
# traded through this path. That guard does not excuse pointing this at anything but a reserved PRACTICE account;
# it is the backstop, not the plan.
#
# Never run this at the same time as a `staging-gates.yml` workflow_dispatch run (Actions tab) -- both place
# orders on the SAME reserved account, and nothing serializes a local run against a concurrent CI run (only
# StagingExecutionCollection serializes suites WITHIN one process). Check the Actions tab is idle first.
#
# USAGE
# -----
#   1. Bring up the local stack: `docker compose up -d` (or the dev-build override), reachable at
#      http://localhost:8080 by default.
#   2. Export the STAGING_* variables below (a local, gitignored file you `source` is fine -- never commit them).
#   3. Run this script from the repo root: `scripts/run-staging-gates-local.sh`
#
# Required environment (identical set to staging-gates.yml -- see StagingConfig for the canonical list):
#   STAGING_API_BASE_URL              e.g. http://localhost:8080
#   STAGING_OPERATOR_EMAIL / STAGING_OPERATOR_PASSWORD    an operator login on the LOCAL instance
#   STAGING_PROJECTX_CREDENTIAL_KEY   the Connection.CredentialKey the local instance's ProjectX credentials use
#   STAGING_PROJECTX_PRACTICE_ACCOUNT the reserved PRACTICE account name
#   STAGING_EXECUTION_INSTRUMENT      e.g. MES
#   STAGING_PROJECTX_API_KEY / STAGING_PROJECTX_API_SECRET   direct PRACTICE credentials for the gateway read
#   STAGING_PROJECTX_API_BASE_URL     optional -- the client defaults it
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.." || exit 1

REQUIRED_VARS=(
  STAGING_API_BASE_URL
  STAGING_OPERATOR_EMAIL
  STAGING_OPERATOR_PASSWORD
  STAGING_PROJECTX_CREDENTIAL_KEY
  STAGING_PROJECTX_PRACTICE_ACCOUNT
  STAGING_EXECUTION_INSTRUMENT
  STAGING_PROJECTX_API_KEY
  STAGING_PROJECTX_API_SECRET
)

missing=()
for var in "${REQUIRED_VARS[@]}"; do
  if [ -z "${!var:-}" ]; then
    missing+=("$var")
  fi
done

if [ "${#missing[@]}" -gt 0 ]; then
  echo "::error::Missing required STAGING_* variables -- the gates would SKIP by construction rather than run:" >&2
  for var in "${missing[@]}"; do
    echo "  - $var" >&2
  done
  echo "Set every variable above before running this script. A skip reported as a pass is exactly the failure" >&2
  echo "gh#1074 exists to end -- this script refuses to start rather than let that happen quietly." >&2
  exit 2
fi

echo "STAGING_PROJECTX_PRACTICE_ACCOUNT=${STAGING_PROJECTX_PRACTICE_ACCOUNT}"
echo "STAGING_EXECUTION_INSTRUMENT=${STAGING_EXECUTION_INSTRUMENT}"
echo
echo "About to place REAL orders on the account above through a LOCAL instance at ${STAGING_API_BASE_URL}."
echo "R-14 is enforced by construction (StagingProjectXGateway.ResolvePracticeAccountId refuses a non-simulated"
echo "account), but that is the backstop -- confirm the account above really is the reserved practice account,"
echo "and that no staging-gates.yml workflow_dispatch run is in flight, before continuing."
echo

echo "Waiting for the local instance to be ready..."
"$(dirname "${BASH_SOURCE[0]}")/verify-deploy.sh" "$STAGING_API_BASE_URL" 10 5

echo
echo "Running the Category=Staging execution gates..."
dotnet test src/MarqSpec.TradingCopilot.IntegrationTests/MarqSpec.TradingCopilot.IntegrationTests.csproj \
  --configuration Release --filter "Category=Staging"
