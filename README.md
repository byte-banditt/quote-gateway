# QuoteGateway

QuoteGateway serves validated quotes from a simulated upstream and keeps the latest successful value in SQLite. It uses a short in-memory cache, bounded retries, and stale fallback when the upstream is unavailable.

## Run

```sh
dotnet run --project src/FakeUpstream --urls http://localhost:5101
dotnet run --project src/QuoteGateway --urls http://localhost:5100
curl http://localhost:5100/quotes/ABC
```

Examples: `curl http://localhost:5100/quotes/MSFT`; invalid symbols such as `abc` return `400`.

## Behavior

`GET /quotes/{symbol}` accepts `^[A-Z]{1,10}$`. A fresh cache hit returns `source=cache`; otherwise the gateway retries timeout, network, and `5xx` failures up to `MaxRetries`, writes success to SQLite, and returns `source=live`. If all attempts fail, a stored value returns `source=stale` with `ageMs`; absent storage returns `503`. Every response contains `symbol`, decimal `price`, UTC `asOfUtc`, `source`, and `ageMs`.

| Setting | Default |
|---|---:|
| Upstream:BaseUrl | http://localhost:5101 |
| Upstream:TimeoutMs | 300 |
| Upstream:MaxRetries | 2 |
| Upstream:BackoffMs | 50 |
| Upstream:CacheTtlMs | 2000 |
| Sqlite:Path | quote-gateway.db |

## Design decisions

A timeout bounds waiting for a failed dependency. Bounded retries with doubling backoff absorb brief failures without unbounded load. A stale price preserves useful service during outages; `source` and `ageMs` expose its staleness. SQLite stores decimal as `TEXT` so binary floating-point cannot change the price. Plain ADO.NET avoids EF Core’s extra mapping and change-tracking machinery for one table.

## Limitations

The cache is per-process and in-memory; SQLite has one writer; there is no request coalescing, so concurrent misses for one symbol each call the upstream; there is no auth; the upstream is simulated; and the load test uses one machine.

## Load-test results

`loadtest/quotes.js` accepts `RATE`, `DURATION`, and `SKIP_WARMUP=1`. `loadtest/run.sh` runs ramp steps at 50, 100, 200, 400, and 800 req/s for 20 s each, and checks both ports before and after every run. It starts each gateway/upstream pair with `CacheTtlMs=0`; this disables the in-memory cache because `AgeMs(...)` is never less than 0. The scenario runs warm SQLite by requesting every symbol once before load. The original setting is retained as `reference` (`CacheTtlMs=2000`, 20 req/s).

Sustainable means zero `dropped_iterations` and p99 < 100 ms. `RATE_MAX` is the highest sustainable ramp step; scenario load is 50% of it. For each scenario, report the measured median of three runs only. No new medians were recorded in this environment: `dotnet` is unavailable, so the gateway and upstream could not run. Therefore no unmeasured values are shown below.

| Scenario | Achieved req/s | dropped_iterations | p50 ms | p95 ms | p99 ms | HTTP 200 | live/cache/stale/503 |
|---|---:|---:|---:|---:|---:|---:|---:|

Topology for the intended run: Linux WSL2 (`6.18.40.1-microsoft-standard-WSL2`, x86_64); gateway and FakeUpstream run as local `dotnet` processes; k6 runs locally and reaches them over `127.0.0.1:5100` and `127.0.0.1:5101`; SQLite is `quote-gateway.db` in the repository working directory (`/home/ayussh/projects/quote-gateway`), on `ext4`; host has 16 CPUs and 4.8 GiB RAM. This environment has `k6` but no `dotnet`, so the topology was not executed here.

Rates are modest and are not production capacity. Scenario C requires warm-up, then a fully stopped upstream; stale fallback is exercised only if retries fail and the warmed SQLite row remains. Its retry latency is measured as the C p95/p99 minus the healthy baseline, not estimated.

## CI

<!-- Add the GitHub Actions status badge after this repository has a remote. -->
