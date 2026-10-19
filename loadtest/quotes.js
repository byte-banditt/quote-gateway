import http from 'k6/http';
import { Counter } from 'k6/metrics';

const symbols = ['ABC', 'MSFT', 'GOOG', 'AMZN', 'TSLA'];
const gateway = __ENV.GATEWAY_URL || 'http://127.0.0.1:5100';
const rate = Number(__ENV.RATE || 20);
const duration = __ENV.DURATION || '20s';

export const sourceLive = new Counter('source_live');
export const sourceCache = new Counter('source_cache');
export const sourceStale = new Counter('source_stale');
export const status200 = new Counter('status_200');
export const status503 = new Counter('status_503');

export const options = {
  summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(95)', 'p(99)'],
  scenarios: { quotes: { executor: 'constant-arrival-rate', rate, timeUnit: '1s', duration, preAllocatedVUs: Number(__ENV.PRE_ALLOCATED_VUS || 100), maxVUs: Number(__ENV.MAX_VUS || 2000) } },
};

export function setup() {
  if (__ENV.SKIP_WARMUP === '1') return;
  for (const symbol of symbols) {
    const response = http.get(`${gateway}/quotes/${symbol}`);
    if (response.status !== 200) throw new Error(`warm-up failed for ${symbol}: ${response.status}`);
  }
}

export default function () {
  const response = http.get(`${gateway}/quotes/${symbols[__ITER % symbols.length]}`);
  if (response.status === 200) {
    status200.add(1);
    const source = JSON.parse(response.body).source;
    if (source === 'live') sourceLive.add(1);
    if (source === 'cache') sourceCache.add(1);
    if (source === 'stale') sourceStale.add(1);
  } else if (response.status === 503) status503.add(1);
}
