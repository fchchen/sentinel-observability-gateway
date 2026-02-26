import http from "k6/http";
import { check, sleep } from "k6";
import { Counter, Rate, Trend } from "k6/metrics";

const BASE_URL = __ENV.BASE_URL || "http://localhost:8080";
const RATE_PER_SECOND = Number(__ENV.RATE || 50);
const DURATION = __ENV.DURATION || "2m";
const PRE_ALLOCATED_VUS = Number(__ENV.PRE_ALLOCATED_VUS || 20);
const MAX_VUS = Number(__ENV.MAX_VUS || 200);
const DUPLICATE_PCT = Number(__ENV.DUPLICATE_PCT || 5);
const INVALID_PCT = Number(__ENV.INVALID_PCT || 2);
const TENANT_COUNT = Number(__ENV.TENANT_COUNT || 5);

const acceptedRate = new Rate("gateway_accept_rate");
const invalidHandledRate = new Rate("gateway_invalid_handled_rate");
const duplicateRequestsTotal = new Counter("load_duplicate_requests_total");
const invalidRequestsTotal = new Counter("load_invalid_requests_total");
const acceptedLatencyMs = new Trend("gateway_accepted_latency_ms");

const duplicatePool = [];

export const options = {
  scenarios: {
    ingest: {
      executor: "constant-arrival-rate",
      rate: RATE_PER_SECOND,
      timeUnit: "1s",
      duration: DURATION,
      preAllocatedVUs: PRE_ALLOCATED_VUS,
      maxVUs: MAX_VUS
    }
  },
  thresholds: {
    http_req_failed: ["rate<0.05"],
    http_req_duration: ["p(95)<500", "p(99)<1000"],
    gateway_accept_rate: ["rate>0.90"],
    gateway_invalid_handled_rate: ["rate>0.95"]
  }
};

function randomInt(maxExclusive) {
  return Math.floor(Math.random() * maxExclusive);
}

function randomHex(length) {
  const alphabet = "0123456789abcdef";
  let result = "";
  for (let i = 0; i < length; i += 1) {
    result += alphabet[randomInt(alphabet.length)];
  }

  return result;
}

function uuidV4() {
  return `${randomHex(8)}-${randomHex(4)}-4${randomHex(3)}-a${randomHex(3)}-${randomHex(12)}`;
}

function tenantId() {
  return `tenant-${1 + randomInt(Math.max(TENANT_COUNT, 1))}`;
}

function createValidRequest() {
  const eventId = uuidV4();
  const streamKey = `order-${100000 + randomInt(900000)}`;
  const key = `k6-${tenantId()}-${eventId}`;
  const body = JSON.stringify({
    eventId,
    tenantId: tenantId(),
    source: "k6-load",
    type: "OrderCreated",
    timestampUtc: new Date().toISOString(),
    schemaVersion: 1,
    streamKey,
    payload: {
      orderId: streamKey,
      amount: Number((Math.random() * 1000).toFixed(2)),
      currency: "USD"
    }
  });

  return { key, body };
}

function createInvalidRequest() {
  const request = createValidRequest();
  const parsed = JSON.parse(request.body);
  delete parsed.eventId;
  return { key: request.key, body: JSON.stringify(parsed) };
}

export default function () {
  const draw = Math.random() * 100;
  let mode = "valid";
  if (draw < INVALID_PCT) {
    mode = "invalid";
  } else if (draw < INVALID_PCT + DUPLICATE_PCT && duplicatePool.length > 0) {
    mode = "duplicate";
  }

  let requestData;
  if (mode === "invalid") {
    invalidRequestsTotal.add(1);
    requestData = createInvalidRequest();
  } else if (mode === "duplicate") {
    duplicateRequestsTotal.add(1);
    requestData = duplicatePool[randomInt(duplicatePool.length)];
  } else {
    requestData = createValidRequest();
    if (duplicatePool.length < 1000) {
      duplicatePool.push(requestData);
    }
  }

  const response = http.post(`${BASE_URL}/v1/events`, requestData.body, {
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": requestData.key
    }
  });

  if (mode === "invalid") {
    const ok = check(response, {
      "invalid event handled with 4xx": (r) => r.status >= 400 && r.status < 500
    });
    invalidHandledRate.add(ok);
  } else {
    const ok = check(response, {
      "valid event accepted": (r) => r.status === 202
    });
    acceptedRate.add(ok);
    acceptedLatencyMs.add(response.timings.duration);
  }

  sleep(0.1);
}
