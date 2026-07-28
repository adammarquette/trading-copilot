#!/usr/bin/env bash
#
# Fails when a key documented in .env.example is not forwarded to a container by docker-compose.yml.
#
# WHY THIS EXISTS (gh#325)
# -----------------------
# The compose `environment:` map is an ALLOWLIST: compose forwards only the keys named there. A key documented
# in .env.example but missing from that map is read for interpolation and then SILENTLY DROPPED -- the operator
# sets it, the app never sees it, and nothing says so.
#
# That has landed three times. gh#236 dropped Flatten__* (the R-13 auto-flatten deadlines) and Execution__* (the
# R-16 fat-finger caps). gh#304 forwarded only Ingestion__Symbols__0 while .env.example said to add __1, __2.
# Three more were caught only because the author happened to remember the trap while writing them. Both landed
# instances failed SAFE -- the built-in defaults stayed armed -- but they failed SILENTLY, which is what
# engineering §9 rules out: "never fabricate, never fail silently."
#
# HOW IT DECIDES
# --------------
# The compose side is taken from `docker compose config`, i.e. the RESOLVED output -- what a container will
# actually receive -- rather than a hand-parse of the source YAML. Fewer ways to be subtly wrong.
#
# A key is "documented" if .env.example has a line `KEY=` (commented or not; the commented examples ARE the
# documentation of what is settable) AND the key looks like a real environment variable:
#   * contains `__`  -- the ASP.NET Core configuration convention (Flatten__…, CheckIn__Instruments__0__Url), or
#   * is SCREAMING_SNAKE -- POSTGRES_USER, OTEL_SERVICE_NAME, GF_SECURITY_ADMIN_USER.
# That second test is what keeps prose out: the flatten block contains "…deliberately with Enabled=false", and a
# looser rule would demand a container variable called `Enabled`.
#
# Keys containing `__` must reach the **app** service specifically -- that is the drift class this guards.
# Everything else need only reach SOME service, because POSTGRES_* belong to `db` and GF_SECURITY_ADMIN_* to
# `grafana`. Known limitation: an app-scoped key with no `__` (OTEL_*) is checked loosely, so forwarding it to
# the wrong service would pass. Tightening that needs a key→service map to maintain, which is a worse trade than
# the gap until a real case appears.
#
# Run it locally exactly as CI does:  ./scripts/check-env-forwarding.sh
set -euo pipefail

cd "$(dirname "$0")/.."

APP_SERVICE="app"
ENV_EXAMPLE=".env.example"

if ! command -v docker >/dev/null 2>&1; then
    echo "check-env-forwarding: docker is required (it resolves the compose file). Skipping is not an option --" >&2
    echo "  a check that quietly does nothing is the failure mode this script exists to prevent." >&2
    exit 2
fi

# Resolve with EVERY declared profile enabled. Profile-gated services (the observability stack) are absent from
# a default `docker compose config`, so without this the check reports their variables as forwarded nowhere --
# a false failure that would teach people to ignore it. Profiles are read from the file so this needs no
# maintenance when one is added.
profile_args=()
while read -r profile; do
    [[ -n "$profile" ]] && profile_args+=(--profile "$profile")
done < <(grep -oE 'profiles:[[:space:]]*\["[^"]+"\]' docker-compose.yml | grep -oE '"[^"]+"' | tr -d '"' | sort -u)

resolved="$(docker compose "${profile_args[@]}" config 2>/dev/null)" || {
    echo "check-env-forwarding: 'docker compose config' failed — the compose file is invalid." >&2
    exit 2
}

# Every environment key, per service, from the resolved output:  "<service> <KEY>"
forwarded="$(printf '%s\n' "$resolved" | awk '
    /^  [A-Za-z0-9_-]+:$/        { service = $1; sub(":", "", service); in_env = 0; next }
    /^    environment:$/         { in_env = 1; next }
    /^    [A-Za-z0-9_-]+:/       { in_env = 0 }
    in_env && /^      [A-Za-z_][A-Za-z0-9_]*:/ {
        key = $1; sub(":", "", key); print service, key
    }
')"

# The OTHER way a value legitimately reaches a container: a top-level `secrets:` entry with an `environment:`
# source, mounted into a service as a file under /run/secrets (gh#245). Compose reads the named variable and
# materialises it, so the operator's .env value DOES arrive -- it simply never appears in an `environment:` map,
# which is the only shape the block above can see.
#
# Found by this check failing on gh#245's own Alertmanager credentials. Suppressing them with an ignore-list
# entry would have been the wrong fix: they are container configuration, they are forwarded, and the checker was
# the thing that was wrong. Secrets are matched WITHOUT a service (any service may mount them) because a secret's
# consumers are listed on the service, not on the secret.
secret_sourced="$(printf '%s\n' "$resolved" | awk '
    /^secrets:$/                              { in_secrets = 1; next }
    /^[A-Za-z0-9_-]+:$/                       { in_secrets = 0 }
    in_secrets && /^    environment: [A-Za-z_][A-Za-z0-9_]*$/ { print $2 }
')"

# Keys that are NOT container configuration, and so are correctly absent from every service.
#
# This list is deliberately tiny and each entry carries its reason. An ignore list is where a check like this
# goes to die -- one unexplained entry invites the next, and eventually it exempts the thing it was written to
# catch. If an addition here is not obviously not-container-config, it is a compose bug being suppressed.
NOT_CONTAINER_CONFIG=(
    # The reviewer GitHub App (gh#141, runbook "Code review identity"): read by tooling on the host to submit a
    # review as the bot, never by the running app. The private key is a FILE PATH for the same reason.
    REVIEWER_APP_ID
    REVIEWER_APP_INSTALLATION_ID
    REVIEWER_APP_PRIVATE_KEY_FILE
    # Selects which published image tag compose PULLS -- consumed by compose itself, not passed to a container.
    IMAGE_TAG
)

# Every key .env.example documents as settable, commented or not.
documented="$(grep -oE '^#?[[:space:]]*[A-Za-z_][A-Za-z0-9_]*=' "$ENV_EXAMPLE" \
    | sed -E 's/^#?[[:space:]]*//; s/=$//' \
    | grep -E '(__|^[A-Z][A-Z0-9_]*$)' \
    | sort -u)"

missing=""
for key in $documented; do
    skip=""
    for ignored in "${NOT_CONTAINER_CONFIG[@]}"; do
        [[ "$key" == "$ignored" ]] && skip="yes" && break
    done
    [[ -n "$skip" ]] && continue

    # A secret-sourced value reaches a container as a mounted file, which satisfies either rule below.
    printf '%s\n' "$secret_sourced" | grep -qx "$key" && continue

    if [[ "$key" == *"__"* ]]; then
        # ASP.NET config key: must reach the app itself, not merely some container.
        printf '%s\n' "$forwarded" | grep -qx "$APP_SERVICE $key" || missing="$missing  $key (not forwarded to '$APP_SERVICE')\n"
    else
        printf '%s\n' "$forwarded" | grep -qE "^[A-Za-z0-9_-]+ $key\$" || missing="$missing  $key (not forwarded to any service)\n"
    fi
done

if [[ -n "$missing" ]]; then
    echo "check-env-forwarding: FAILED — $ENV_EXAMPLE documents keys that docker-compose.yml does not forward." >&2
    echo "" >&2
    printf "%b" "$missing" >&2
    echo "" >&2
    echo "An operator who sets one of these gets NO error and NO effect — the value is read for interpolation" >&2
    echo "and dropped, because the compose 'environment:' map is an allowlist (gh#236, gh#304)." >&2
    echo "" >&2
    echo "Fix: add the key to the app service's 'environment:' map in docker-compose.yml. Prefer the null" >&2
    echo "pass-through form (\`Key__Name:\` with no value) for anything the app already defaults — an empty" >&2
    echo "string BINDS and overwrites that default. Indexed lists are enumerated and capped on purpose; a new" >&2
    echo "index needs its own line, and the cap belongs in $ENV_EXAMPLE so it is not a surprise." >&2
    exit 1
fi

echo "check-env-forwarding: OK — every documented key reaches a container ($(printf '%s\n' "$documented" | wc -l | tr -d ' ') checked)."
