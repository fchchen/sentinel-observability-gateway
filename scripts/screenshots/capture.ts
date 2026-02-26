/**
 * Playwright screenshot capture for Sentinel Observability Gateway.
 *
 * Prerequisites (must be running before this script):
 *   docker compose up -d          — Kafka, Postgres, Prometheus (:19090), Grafana (:13000)
 *   dotnet run --project src/gateway-api        — :8080
 *   dotnet run --project src/processor-worker   — background worker
 *   dotnet run --project src/query-api           — :8081
 *   dotnet run --project src/realtime-hub        — :8082
 *   cd src/web-ui && npm start                   — :4200
 *
 * Usage:
 *   npx tsx capture.ts
 */

import { chromium, type Page } from "playwright";
import { randomUUID } from "node:crypto";
import path from "node:path";
import { fileURLToPath } from "node:url";
import fs from "node:fs";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const OUTPUT_DIR = path.resolve(__dirname, "../../docs/screenshots");

const GATEWAY_URL = "http://localhost:8080";
const QUERY_URL = "http://localhost:8081";
const GRAFANA_URL = "http://localhost:13000";
const PROMETHEUS_URL = "http://localhost:19090";
const ANGULAR_URL = "http://localhost:4200";

const VIEWPORT = { width: 1280, height: 800 };
const EVENT_COUNT = 50;
const TENANTS = ["tenant-alpha", "tenant-beta", "tenant-gamma"];

// ── Helpers ──────────────────────────────────────────────────────────

function log(msg: string) {
  console.log(`[screenshots] ${msg}`);
}

async function sleep(ms: number) {
  return new Promise((r) => setTimeout(r, ms));
}

// ── Traffic Seeding ──────────────────────────────────────────────────

async function seedTraffic() {
  log(`Seeding ${EVENT_COUNT} events across ${TENANTS.length} tenants...`);

  const types = ["OrderCreated", "UserSignedUp", "PaymentProcessed", "InventoryUpdated", "ShipmentDispatched"];
  let accepted = 0;
  let failed = 0;

  for (let i = 0; i < EVENT_COUNT; i++) {
    const tenantId = TENANTS[i % TENANTS.length];
    const eventId = randomUUID();
    const idempotencyKey = randomUUID();
    const type = types[i % types.length];

    const body = {
      eventId,
      tenantId,
      source: "screenshot-capture",
      type,
      timestampUtc: new Date().toISOString(),
      schemaVersion: 1,
      streamKey: `stream-${String(i).padStart(4, "0")}`,
      payload: {
        index: i,
        description: `Test event ${i} for screenshots`,
        amount: Math.round(Math.random() * 10000) / 100,
      },
    };

    try {
      const res = await fetch(`${GATEWAY_URL}/v1/events`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Idempotency-Key": idempotencyKey,
        },
        body: JSON.stringify(body),
      });

      if (res.status === 202) {
        accepted++;
      } else {
        failed++;
        log(`  Event ${i}: HTTP ${res.status}`);
      }
    } catch (err) {
      failed++;
      log(`  Event ${i}: ${err}`);
    }
  }

  log(`Seeding complete: ${accepted} accepted, ${failed} failed`);

  if (accepted === 0) {
    throw new Error("No events were accepted — is the gateway running on :8080?");
  }

  log("Waiting 12s for processor + Prometheus scrape...");
  await sleep(12_000);
}

// ── JSON Rendering ───────────────────────────────────────────────────

function jsonToStyledHtml(title: string, data: unknown): string {
  const json = JSON.stringify(data, null, 2);

  // Syntax-highlight JSON
  const highlighted = json
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"([^"]+)":/g, '<span class="key">"$1"</span>:')
    .replace(/: "([^"]*)"/g, ': <span class="string">"$1"</span>')
    .replace(/: (\d+\.?\d*)/g, ': <span class="number">$1</span>')
    .replace(/: (true|false)/g, ': <span class="bool">$1</span>')
    .replace(/: (null)/g, ': <span class="null">$1</span>');

  return `<!DOCTYPE html>
<html>
<head>
<style>
  * { margin: 0; padding: 0; box-sizing: border-box; }
  body {
    background: #1e1e2e;
    color: #cdd6f4;
    font-family: 'JetBrains Mono', 'Fira Code', 'Consolas', monospace;
    padding: 32px;
  }
  h1 {
    font-size: 18px;
    font-weight: 600;
    color: #89b4fa;
    margin-bottom: 8px;
    letter-spacing: 0.5px;
  }
  .subtitle {
    font-size: 12px;
    color: #6c7086;
    margin-bottom: 20px;
  }
  pre {
    background: #181825;
    border: 1px solid #313244;
    border-radius: 8px;
    padding: 20px;
    font-size: 13px;
    line-height: 1.6;
    overflow-x: auto;
    white-space: pre;
  }
  .key { color: #89b4fa; }
  .string { color: #a6e3a1; }
  .number { color: #fab387; }
  .bool { color: #f38ba8; }
  .null { color: #6c7086; font-style: italic; }
</style>
</head>
<body>
  <h1>${title}</h1>
  <div class="subtitle">Sentinel Observability Gateway</div>
  <pre>${highlighted}</pre>
</body>
</html>`;
}

// ── Screenshot Captures ──────────────────────────────────────────────

async function captureAngularDashboard(page: Page) {
  log("1/5 Capturing Angular dashboard...");
  await page.goto(ANGULAR_URL, { waitUntil: "networkidle" });

  // Wait for the grid layout to render with event data
  try {
    await page.waitForSelector(".grid, .dashboard, app-root table, app-root .events", {
      timeout: 10_000,
    });
  } catch {
    log("  Warning: grid selector not found, capturing current state");
  }

  // Extra settle time for SignalR connections and animations
  await sleep(2_000);

  await page.screenshot({
    path: path.join(OUTPUT_DIR, "01-angular-dashboard.png"),
    fullPage: false,
  });
  log("  Saved 01-angular-dashboard.png");
}

async function capturePipelineHealth(page: Page) {
  log("2/5 Capturing pipeline health...");

  const res = await fetch(`${QUERY_URL}/v1/pipeline/health`);
  if (!res.ok) throw new Error(`Pipeline health returned ${res.status}`);
  const data = await res.json();

  await page.setContent(jsonToStyledHtml("Pipeline Health", data));
  await sleep(500);

  // Clip to content to avoid large empty space below the JSON block
  const body = page.locator("body");
  const box = await body.boundingBox();
  const preEl = page.locator("pre");
  const preBox = await preEl.boundingBox();
  const clipHeight = preBox ? preBox.y + preBox.height + 32 : box?.height ?? 800;

  await page.screenshot({
    path: path.join(OUTPUT_DIR, "02-pipeline-health.png"),
    clip: { x: 0, y: 0, width: VIEWPORT.width, height: Math.min(clipHeight, VIEWPORT.height) },
  });
  log("  Saved 02-pipeline-health.png");
}

async function captureRecentEvents(page: Page) {
  log("3/5 Capturing recent events...");

  const res = await fetch(`${QUERY_URL}/v1/events/recent?limit=5`);
  if (!res.ok) throw new Error(`Recent events returned ${res.status}`);
  const data = await res.json();

  await page.setContent(jsonToStyledHtml("Recent Events", data));
  await sleep(500);

  await page.screenshot({
    path: path.join(OUTPUT_DIR, "03-recent-events.png"),
    fullPage: false,
  });
  log("  Saved 03-recent-events.png");
}

async function captureGrafanaDashboard(page: Page) {
  log("4/5 Capturing Grafana dashboard...");

  // Login
  await page.goto(`${GRAFANA_URL}/login`, { waitUntil: "networkidle" });
  await page.fill('input[name="user"]', "admin");
  await page.fill('input[name="password"]', "admin");
  await page.click('button[type="submit"]');

  // Dismiss password-change prompt if it appears
  try {
    const skipBtn = page.locator('a[href*="skip"], button:has-text("Skip")');
    await skipBtn.waitFor({ timeout: 5_000 });
    await skipBtn.click();
  } catch {
    // No prompt — that's fine
  }

  await page.waitForURL(/.*(?!login).*/, { timeout: 10_000 });

  // Navigate to dashboard
  await page.goto(`${GRAFANA_URL}/d/sentinel-overview/sentinel-pipeline-slo?orgId=1&refresh=5s`, {
    waitUntil: "networkidle",
  });

  // Wait for a panel to render — look for the "Gateway RPS" panel title
  try {
    await page.waitForSelector('[data-testid="data-testid Panel header Gateway RPS"], [aria-label*="Gateway RPS"], text="Gateway RPS"', {
      timeout: 15_000,
    });
  } catch {
    log("  Warning: Gateway RPS panel not found by selector, waiting extra time");
    await sleep(5_000);
  }

  // Let panels finish rendering
  await sleep(3_000);

  await page.screenshot({
    path: path.join(OUTPUT_DIR, "04-grafana-dashboard.png"),
    fullPage: false,
  });
  log("  Saved 04-grafana-dashboard.png");
}

async function capturePrometheusTargets(page: Page) {
  log("5/5 Capturing Prometheus targets...");

  await page.goto(`${PROMETHEUS_URL}/targets`, { waitUntil: "networkidle" });

  // Wait for target table to render
  try {
    await page.waitForSelector('table, [class*="target"], a[href*="endpoint"]', {
      timeout: 10_000,
    });
  } catch {
    log("  Warning: targets table not found, capturing current state");
  }

  await sleep(2_000);

  await page.screenshot({
    path: path.join(OUTPUT_DIR, "05-prometheus-targets.png"),
    fullPage: false,
  });
  log("  Saved 05-prometheus-targets.png");
}

// ── Main ─────────────────────────────────────────────────────────────

async function main() {
  log("Starting screenshot capture...");

  // Ensure output directory exists
  fs.mkdirSync(OUTPUT_DIR, { recursive: true });

  // Seed traffic first
  await seedTraffic();

  // Launch browser
  const browser = await chromium.launch({ headless: true });
  const context = await browser.newContext({
    viewport: VIEWPORT,
    deviceScaleFactor: 2, // Retina-quality screenshots
  });
  const page = await context.newPage();

  try {
    await captureAngularDashboard(page);
    await capturePipelineHealth(page);
    await captureRecentEvents(page);
    await captureGrafanaDashboard(page);
    await capturePrometheusTargets(page);

    log("All 5 screenshots captured successfully!");
    log(`Output: ${OUTPUT_DIR}`);
  } finally {
    await browser.close();
  }
}

main().catch((err) => {
  console.error("[screenshots] Fatal error:", err);
  process.exit(1);
});
