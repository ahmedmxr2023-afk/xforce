const isProd = process.env.NODE_ENV === "production";

let cachedSecret: string | null = null;

function read(name: string): string | undefined {
  const value = process.env[name];
  if (typeof value === "string" && value.trim().length > 0) return value.trim();
  return undefined;
}

function requireInProduction(name: string, devFallback: string): string {
  const value = read(name);
  if (value) return value;
  if (isProd) {
    throw new Error(
      `Missing required environment variable ${name}. Set it in your host's environment before starting the server.`,
    );
  }
  return devFallback;
}

export function sessionSecret(): string {
  if (cachedSecret) return cachedSecret;
  cachedSecret = requireInProduction("SESSION_SECRET", "dev-only-insecure-session-secret");
  return cachedSecret;
}

export function databasePath(): string {
  return requireInProduction("DATABASE_PATH", "./data/xforcemsw.db");
}

export function storageDir(): string {
  return requireInProduction("STORAGE_DIR", "./data/storage");
}

// Directory the engine writes evidence into (report.json, roundtrip.json) and
// the directory holding tests.trx. Forwarded to the engine as query params so a
// hosted frontend can point at an engine on another machine. Optional: the
// engine also reads the same variables from its own environment.
export function engineOutputDir(): string | undefined {
  return read("MSW_OUTPUT_DIR");
}

export function engineStatusDir(): string | undefined {
  return read("MSW_STATUS_DIR");
}

export function appUrl(): string {
  return read("APP_URL") ?? (isProd ? "" : "http://localhost:3000");
}

export function siteName(): string {
  return read("SITE_NAME") ?? "XforceMsw";
}

export function isProduction(): boolean {
  return isProd;
}

export function adminEmails(): string[] {
  const raw = read("ADMIN_EMAILS") ?? read("ADMIN_EMAIL") ?? "";
  return raw
    .split(",")
    .map((entry) => entry.trim().toLowerCase())
    .filter((entry) => entry.length > 0);
}

export function engineUrl(): string | null {
  return read("MSW_ENGINE_URL") ?? null;
}

export function engineToken(): string | null {
  return read("MSW_ENGINE_TOKEN") ?? null;
}

/**
 * Optional bootstrap administrator, created on every cold start when the account
 * is missing.
 *
 * Hosts without a persistent disk lose the database on redeploy, which would
 * also lose the only admin account. Setting BOOTSTRAP_ADMIN_EMAIL and
 * BOOTSTRAP_ADMIN_PASSWORD recreates it on boot. Existing accounts are never
 * rewritten beyond their role, so a password changed in the UI stays changed.
 */
export function bootstrapAdmin(): { email: string; password: string } | null {
  const email = read("BOOTSTRAP_ADMIN_EMAIL")?.toLowerCase();
  const password = read("BOOTSTRAP_ADMIN_PASSWORD");
  if (!email || !password) return null;
  return { email, password };
}

/**
 * Shared secret that unlocks the mock payment provider.
 *
 * Checkout is refused unless this is set, and the request must echo the same
 * value. A mock provider that issues real licenses is a "pay nothing, get a
 * license" hole, so it stays off unless somebody deliberately turns it on.
 */
export function mockBillingToken(): string | null {
  return read("MOCK_BILLING_TOKEN") ?? null;
}

export function mockBillingEnabled(): boolean {
  return mockBillingToken() !== null;
}
