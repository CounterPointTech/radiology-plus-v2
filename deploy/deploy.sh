#!/usr/bin/env bash
#
# Deploy (or roll back) one Radiology Plus stack.
#
#   ./deploy.sh test sha-1a2b3c4        deploy that image tag to the test stack
#   ./deploy.sh prod sha-9f8e7d6        deploy to prod
#   ./deploy.sh prod sha-<previous>     roll back — same command, earlier tag
#
# Rollback is just a redeploy of a previous tag, which is the whole reason every image
# is tagged with an immutable commit sha and never with `latest`.
#
# Lives at /opt/radplus/deploy.sh on the server. Migrations run automatically: the
# migrator is a one-shot service the other containers wait on.

set -euo pipefail

STACK="${1:-}"
TAG="${2:-}"
ROOT="${RADPLUS_ROOT:-/opt/radplus}"

usage() {
    echo "usage: $0 <dev|test|prod> <image-tag>" >&2
    echo "example: $0 test sha-1a2b3c4" >&2
    exit 2
}

[ -n "$STACK" ] && [ -n "$TAG" ] || usage

case "$STACK" in
    dev|test|prod) ;;
    *) echo "error: unknown stack '$STACK'" >&2; usage ;;
esac

ENV_FILE="$ROOT/env/$STACK.env"
COMPOSE_FILE="$ROOT/compose.yaml"
PROJECT="radplus-$STACK"

[ -f "$ENV_FILE" ]     || { echo "error: missing $ENV_FILE" >&2; exit 1; }
[ -f "$COMPOSE_FILE" ] || { echo "error: missing $COMPOSE_FILE" >&2; exit 1; }

# The env file carries the database password, the JWT secret and the encryption key.
# Anything less strict than 0600 is a finding.
PERMS="$(stat -c '%a' "$ENV_FILE")"
if [ "$PERMS" != "600" ]; then
    echo "error: $ENV_FILE is mode $PERMS; expected 600" >&2
    exit 1
fi

compose() {
    docker compose -p "$PROJECT" --env-file "$ENV_FILE" -f "$COMPOSE_FILE" "$@"
}

PREVIOUS="$(compose config 2>/dev/null | grep -oP 'radplus-api:\K\S+' | head -1 || true)"

echo "==> deploying $PROJECT at tag $TAG (previous: ${PREVIOUS:-unknown})"
export TAG

# Fail before touching the running stack if a tag does not exist in the registry.
echo "==> pulling images"
compose pull --quiet

echo "==> applying migrations and starting services"
# The migrator runs to completion first; the four hosts depend on
# service_completed_successfully, so a failed migration stops the deploy here rather
# than leaving half the stack on a schema it does not understand.
compose up -d --remove-orphans

echo "==> waiting for health"
deadline=$((SECONDS + 180))
while [ $SECONDS -lt $deadline ]; do
    unhealthy="$(compose ps --format '{{.Service}} {{.Status}}' \
                 | grep -Ev 'healthy|migrator' | grep -c 'Up' || true)"
    starting="$(compose ps --format '{{.Status}}' | grep -c 'health: starting' || true)"
    if [ "$starting" = "0" ]; then break; fi
    sleep 5
done

compose ps --format 'table {{.Service}}\t{{.Status}}'

if compose ps --format '{{.Status}}' | grep -q 'unhealthy'; then
    echo "error: one or more services are unhealthy after deploy" >&2
    echo "roll back with: $0 $STACK ${PREVIOUS:-<previous-tag>}" >&2
    exit 1
fi

echo "==> pruning dangling images"
docker image prune -f >/dev/null

echo "==> $PROJECT is running tag $TAG"
echo "    roll back with: $0 $STACK ${PREVIOUS:-<previous-tag>}"
