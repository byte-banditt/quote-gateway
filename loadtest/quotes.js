import http from 'k6/http';
import { Counter } from 'k6/metrics';

const sourceCounts = {
  live: new Counter('source_live'),
  cache: new Counter('source_cache'),
  stale: new Counter('source_stale'),
};

export const options = {
  summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(90)', 'p(95)', 'p(99)'],
  scenarios: { quotes: { executor: 'constant-arrival-rate', rate: 20, timeUnit: '1s', duration: '30s', preAllocatedVUs: 20, maxVUs: 100 } },
};

export default function () {
  const symbols = ['ABC', 'MSFT', 'GOOG', 'AMZN', 'TSLA'];
  const response = http.get(`${__ENV.GATEWAY_URL || 'http://127.0.0.1:5100'}/quotes/${symbols[__ITER % symbols.length]}`);
  if (response.status === 200) {
    const source = JSON.parse(response.body).source;
    if (sourceCounts[source]) sourceCounts[source].add(1);
  }
}
