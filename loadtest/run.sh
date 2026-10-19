#!/usr/bin/env bash
set -Eeuo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
RESULTS="$ROOT/loadtest/results"
GATEWAY_PORT=${GATEWAY_PORT:-5100}; UPSTREAM_PORT=${UPSTREAM_PORT:-5101}
mkdir -p "$RESULTS"
die() { echo "ERROR: $*" >&2; exit 1; }
port_free() { ! ss -ltn "sport = :$1" | tail -n +2 | grep -q .; }
assert_ports_free() { port_free "$GATEWAY_PORT" || die "port $GATEWAY_PORT is occupied"; port_free "$UPSTREAM_PORT" || die "port $UPSTREAM_PORT is occupied"; }
assert_stopped() { port_free "$GATEWAY_PORT" || die "gateway still owns port $GATEWAY_PORT"; port_free "$UPSTREAM_PORT" || die "upstream still owns port $UPSTREAM_PORT"; }
gateway_pid=; upstream_pid=
stop_all() {
  [[ -n "$gateway_pid" ]] && kill "$gateway_pid" 2>/dev/null || true
  [[ -n "$upstream_pid" ]] && kill "$upstream_pid" 2>/dev/null || true
  wait "$gateway_pid" 2>/dev/null || true; wait "$upstream_pid" 2>/dev/null || true
  gateway_pid=; upstream_pid=; assert_stopped
}
trap stop_all EXIT INT TERM
start_services() {
  assert_ports_free
  (cd "$ROOT" && FAIL_RATE="${FAIL_RATE:-0}" DELAY_MS="${DELAY_MS:-20}" dotnet run --project src/FakeUpstream --no-launch-profile --urls "http://127.0.0.1:$UPSTREAM_PORT") & upstream_pid=$!
  (cd "$ROOT" && dotnet run --project src/QuoteGateway --no-launch-profile --urls "http://127.0.0.1:$GATEWAY_PORT" --Upstream:BaseUrl="http://127.0.0.1:$UPSTREAM_PORT" --Upstream:CacheTtlMs="${CACHE_TTL_MS:-0}") & gateway_pid=$!
  for _ in {1..100}; do curl -fsS "http://127.0.0.1:$GATEWAY_PORT/quotes/ABC" >/dev/null 2>&1 && return; sleep 0.1; done
  die "services did not become ready"
}
run_once() {
  local name=$1 rate=$2
  start_services
  run_k6 "$name" "$rate"
  stop_all
}
run_k6() {
  local name=$1 rate=$2; shift 2
  k6 run --out "json=$RESULTS/$name.json" "$ROOT/loadtest/quotes.js" -e RATE="$rate" -e DURATION="${DURATION:-20s}" -e GATEWAY_URL="http://127.0.0.1:$GATEWAY_PORT" "$@"
}
: "${1:?usage: run.sh ramp|scenario|reference}"
case "$1" in
  ramp) for rate in 50 100 200 400 800; do DURATION=20s run_once "ramp-$rate" "$rate"; done ;;
  reference) CACHE_TTL_MS=2000 DURATION=30s run_once original-20 20 ;;
  scenario)
    : "${RATE_MAX:?set RATE_MAX to measured sustainable maximum}"
    rate=$((RATE_MAX / 2))
    for fail in 0 0.2; do for run in 1 2 3; do FAIL_RATE=$fail CACHE_TTL_MS=0 run_once "scenario-${fail//./p}-$run" "$rate"; done; done
    for run in 1 2 3; do
      FAIL_RATE=0 CACHE_TTL_MS=0 start_services
      run_k6 "scenario-c-warmup-$run" 1
      kill "$upstream_pid" 2>/dev/null || true; wait "$upstream_pid" 2>/dev/null || true; upstream_pid=
      assert_stopped_upstream() { port_free "$UPSTREAM_PORT" || die "upstream still owns port $UPSTREAM_PORT"; }
      assert_stopped_upstream
      run_k6 "scenario-c-$run" "$rate" -e SKIP_WARMUP=1
      stop_all
    done
    ;;
  *) die "unknown mode: $1" ;;
esac
