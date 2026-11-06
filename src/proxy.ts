import { NextResponse } from "next/server";
import { DOCUMENT_CACHE_CONTROL } from "@/lib/cache-policy";

/**
 * Keeps build-specific responses out of shared caches.
 *
 * Next.js serves fully prerendered pages (for example `/`) with
 * `Cache-Control: s-maxage=31536000`, i.e. "a CDN may keep this for a year". The HTML, and
 * the RSC payload behind a client-side navigation, name the hashed chunks of the build that
 * produced them; a later deploy deletes those files. If an edge cache keeps serving the old
 * document, the browser asks for a chunk that no longer exists, gets a 404, and React fails
 * the route with `ChunkLoadError`.
 *
 * Every route that reaches this proxy is either a document, an RSC payload, or an API
 * response — all of which are per-build or per-user and must be revalidated. Content-hashed
 * assets under `/_next/static` are excluded by the matcher so they keep their immutable,
 * long-lived caching.
 */
export function proxy() {
  const response = NextResponse.next();
  response.headers.set("Cache-Control", DOCUMENT_CACHE_CONTROL);
  return response;
}

export const config = {
  // Everything except immutable build output and public brand files.
  matcher: ["/((?!_next/static|_next/image|brand/|favicon.ico|icon.png).*)"],
};
