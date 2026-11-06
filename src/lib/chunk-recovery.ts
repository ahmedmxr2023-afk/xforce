/**
 * Inline bootstrap that recovers from stale-chunk (version skew) failures.
 *
 * Deploying replaces the hashed files under `/_next/static/chunks`. A tab that was opened
 * before the deploy still holds the previous HTML/RSC payload, so the next client-side
 * navigation requests a chunk that no longer exists, the request 404s, and React's lazy
 * loader throws `ChunkLoadError`. Without a recovery path the page is stuck on the opaque
 * "application failed to load" screen until the visitor knows to hard-refresh.
 *
 * `deploymentId` in `next.config.ts` prevents most of this by making the server detect the
 * skew and force a full navigation. This script is the second line of defence for the cases
 * `deploymentId` cannot cover: a chunk that vanishes mid-session, an edge cache that still
 * holds the old document, and hosts where no deployment id is available.
 */

/** Session storage key holding the timestamp and count of automatic recoveries. */
export const CHUNK_RECOVERY_KEY = "xforce:chunk-recovery";

/** Give up after this many reloads inside {@link CHUNK_RECOVERY_WINDOW_MS}. */
export const CHUNK_RECOVERY_MAX_ATTEMPTS = 2;

/** Window in which repeated failures count towards the same recovery budget. */
export const CHUNK_RECOVERY_WINDOW_MS = 30_000;

/**
 * Plain string rather than a function because it is inlined before any Next.js module runs.
 * Kept dependency-free and defensive: every storage access is wrapped, since storage can be
 * disabled outright and a throwing bootstrap script would break the page it is meant to save.
 */
export const chunkRecoveryScript = `(function () {
  var KEY = ${JSON.stringify(CHUNK_RECOVERY_KEY)};
  var MAX_ATTEMPTS = ${CHUNK_RECOVERY_MAX_ATTEMPTS};
  var WINDOW_MS = ${CHUNK_RECOVERY_WINDOW_MS};
  var PATTERN = /ChunkLoadError|Loading (CSS )?chunk|Failed to load chunk|Failed to fetch dynamically imported module|Importing a module script failed|error loading dynamically imported module/i;

  function describe(value) {
    if (!value) return "";
    if (typeof value === "string") return value;
    return (value.name || "") + " " + (value.message || "");
  }

  function isChunkFailure(value) {
    return PATTERN.test(describe(value));
  }

  function readBudget() {
    try {
      var raw = window.sessionStorage.getItem(KEY);
      if (!raw) return { at: 0, attempts: 0 };
      var parsed = JSON.parse(raw);
      if (!parsed || typeof parsed.at !== "number" || typeof parsed.attempts !== "number") {
        return { at: 0, attempts: 0 };
      }
      return parsed;
    } catch (error) {
      return { at: 0, attempts: 0 };
    }
  }

  function writeBudget(budget) {
    try {
      window.sessionStorage.setItem(KEY, JSON.stringify(budget));
    } catch (error) {
      /* Storage unavailable; the in-page guard below still prevents a second reload. */
    }
  }

  var reloading = false;

  function recover() {
    if (reloading) return;
    var now = Date.now();
    var budget = readBudget();
    if (now - budget.at > WINDOW_MS) budget = { at: now, attempts: 0 };
    if (budget.attempts >= MAX_ATTEMPTS) return;

    budget.attempts += 1;
    budget.at = now;
    writeBudget(budget);
    reloading = true;

    // A reload revalidates the document, which is what replaces the stale HTML that points at
    // the deleted chunk. If a CDN still serves the old document, the next attempt falls back to
    // a cache-busting navigation so the request cannot be answered from any cache.
    if (budget.attempts === 1) {
      window.location.reload();
      return;
    }
    try {
      var url = new URL(window.location.href);
      url.searchParams.set("_fresh", String(now));
      window.location.replace(url.toString());
    } catch (error) {
      window.location.reload();
    }
  }

  window.addEventListener(
    "error",
    function (event) {
      var target = event.target;
      if (target && target !== window && target.tagName) {
        var tag = String(target.tagName).toUpperCase();
        // Scripts are always fatal (a chunk or the runtime failed to download). Stylesheets are
        // fatal too, but preloads for images/fonts are not, so check what the link is for.
        if (tag === "SCRIPT") {
          recover();
          return;
        }
        if (tag === "LINK") {
          var rel = String(target.rel || "").toLowerCase();
          var as = String(target.as || "").toLowerCase();
          if (rel === "stylesheet" || (rel === "preload" && as === "style")) {
            recover();
            return;
          }
        }
      }
      if (isChunkFailure(event.error) || isChunkFailure(event.message)) recover();
    },
    true,
  );

  window.addEventListener("unhandledrejection", function (event) {
    if (isChunkFailure(event.reason)) recover();
  });
})();`;

/** True when an error is a failed download of a stale hashed chunk. */
export function isChunkLoadFailure(error: unknown): boolean {
  if (!error) return false;
  const value = error as { name?: string; message?: string };
  const text = typeof error === "string" ? error : `${value.name ?? ""} ${value.message ?? ""}`;
  return /ChunkLoadError|Loading (CSS )?chunk|Failed to load chunk|Failed to fetch dynamically imported module|Importing a module script failed|error loading dynamically imported module/i.test(
    text,
  );
}

/**
 * Reloads the current document, bypassing any cache that may still hold HTML referencing a
 * deleted build. Clears the automatic-recovery budget so a visitor who lands on an error
 * boundary gets a real retry instead of being told to wait.
 */
export function reloadDocument(): void {
  try {
    window.sessionStorage.removeItem(CHUNK_RECOVERY_KEY);
  } catch {
    /* Storage unavailable; the navigation below is still safe. */
  }
  try {
    const url = new URL(window.location.href);
    url.searchParams.set("_fresh", String(Date.now()));
    window.location.replace(url.toString());
  } catch {
    window.location.reload();
  }
}

