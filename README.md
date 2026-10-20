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

Sustainable means zero `dropped_iterations` and p99 < 100 ms. `RATE_MAX=50` req/s: 50 req/s is sustainable, while 100 req/s has measured p99 `170.724219` ms. Scenario load is 50% of `RATE_MAX`, or 25 req/s. Scenario rows below are medians of 3 clean runs; source splits are median counts.

| Ramp step | Achieved req/s | dropped_iterations | p95 ms | p99 ms |
|---:|---:|---:|---:|---:|
| 50 | 50.044996 | 0 | 28.742001 | 29.878681 |
| 100 | 99.985002 | 0 | 28.219864 | 170.724219 |
| 200 | 197.160883 | 0 | 365.452384 | 781.537136 |
| 400 | 288.220987 | 1364 | 4785.353513 | 5429.265712 |
| 800 | 351.199762 | 6545 | 6309.197053 | 7693.594804 |

| Scenario | Achieved req/s | dropped_iterations | p50 ms | p95 ms | p99 ms | HTTP 200 | live/cache/stale/503 |
|---|---:|---:|---:|---:|---:|---:|---:|
| A: `FAIL_RATE=0`, `DELAY_MS=20` | 25.060144 | 0 | 24.677594 | 27.328100 | 29.289321 | 100% | 500/0/0/0 |
| B: `FAIL_RATE=0.2`, `DELAY_MS=20` | 25.055011 | 0 | 24.914118 | 172.873500 | 249.733695 | 100% | 499/0/2/0 |
| C: upstream down after warm-up | 25.077585 | 0 | 153.336639 | 156.446349 | 158.434371 | 100% | 0/0/501/0 |
| Original reference: `CacheTtlMs=2000`, 20 req/s | 20.018653 | 0 | 0.716942 | 1.892537 | 27.213970 | 100% | 18/583/0/0 |

Topology for the run: Linux WSL2 (`6.18.40.1-microsoft-standard-WSL2`, x86_64); gateway and FakeUpstream ran as local `dotnet` processes; k6 ran locally and reached them over `127.0.0.1:5100` and `127.0.0.1:5101`; SQLite was `quote-gateway.db` in the repository working directory (`/home/ayussh/projects/quote-gateway`), on `ext4`; host had 16 CPUs and 4.8 GiB RAM.

Rates are modest and are not production capacity. Scenario C exercised stale fallback in all 3 runs: `501` stale responses per run, with no live/cache/503 responses. Relative to A, retries added measured median latency of `128.659045` ms at p50, `129.118249` ms at p95, and `129.145050` ms at p99.

## CI

<!-- Add the GitHub Actions status badge after this repository has a remote. -->
