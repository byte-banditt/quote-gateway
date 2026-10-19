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

The cache is single-node and in-memory; SQLite has one writer; there is no auth; the upstream is simulated; and the load test uses one machine.

## Load-test results

The script uses a fixed 20 requests/s for 30 s over five symbols. Run separately with `FAIL_RATE=0, DELAY_MS=20` and `FAIL_RATE=0.2, DELAY_MS=20`; each scenario is run 3 times and reported by median. Raw summaries belong in `loadtest/results` and must not be edited.

| Scenario | Requests/s | p50 ms | p95 ms | p99 ms | HTTP 200 | live/cache/stale |
|---|---:|---:|---:|---:|---:|---:|
| Healthy (2 verified runs) | 20.0053 | 0.7954 | 11.6549 | 33.6446 | 100% | 30.5/570/0 median counts |
| 20% failures (3 runs) | 19.9586 | 0.9242 | 25.7303 | 38.4551 | 100% | 37/564/0 median counts |

Healthy third run was not accepted: an old process held port `5101`, contaminating the result. The three raw `20% failures` summaries and the two accepted healthy summaries remain unchanged in `loadtest/results`; all numbers above come from those files.

## CI

<!-- Add the GitHub Actions status badge after this repository has a remote. -->
