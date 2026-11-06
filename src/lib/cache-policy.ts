/**
 * Shared cache policy for build-specific responses.
 *
 * Next.js serves fully prerendered pages with `Cache-Control: s-maxage=31536000`. That HTML
 * names the hashed chunks of the build that produced it, and a later deploy deletes those
 * files; an edge cache holding the document for a year keeps handing out references to chunks
 * that no longer exist, which surfaces as `ChunkLoadError` on the next client-side navigation.
 *
 * Content-hashed assets under `/_next/static` keep their immutable caching; only documents,
 * RSC payloads and API responses use this policy.
 */
export const DOCUMENT_CACHE_CONTROL = "private, no-cache, no-store, max-age=0, must-revalidate";
