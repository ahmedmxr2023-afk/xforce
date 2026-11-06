import type { NextConfig } from "next";

/**
 * Version-skew guard.
 *
 * Every deploy replaces the hashed chunks under `/_next/static`. A visitor whose tab was
 * opened before the deploy keeps an old HTML/RSC payload, and its next client-side
 * navigation requests a chunk that no longer exists — a 404 and `ChunkLoadError`, which
 * shows up as "the application failed to load" (and as `Refused to execute script ... MIME
 * type text/plain`, because the 404 body is served as text/plain).
 *
 * When `deploymentId` is set, Next.js tags asset URLs with `?dpl=`, sends the id on
 * navigation requests, and forces a full navigation instead of a client-side transition when
 * the server's id differs. The tab then downloads the current HTML and chunks in one piece.
 *
 * Railway exposes the commit SHA in both build and runtime environments, so the value baked
 * into the client matches the value the server compares against. When the variable is absent
 * (local dev, other hosts) the option is omitted, leaving default behaviour intact.
 */
const deploymentId =
  process.env.NEXT_DEPLOYMENT_ID ||
  process.env.RAILWAY_GIT_COMMIT_SHA ||
  process.env.VERCEL_GIT_COMMIT_SHA ||
  undefined;

const nextConfig: NextConfig = {
  deploymentId,
  serverExternalPackages: ["better-sqlite3"],
  poweredByHeader: false,
  experimental: {
    // Ladder drawings and MSW bundles can be large; keep server actions generous
    // while staying under typical platform limits.
    serverActions: {
      bodySizeLimit: "2mb",
    },
  },
  async headers() {
    return [
      {
        source: "/(.*)",
        headers: [
          { key: "X-Content-Type-Options", value: "nosniff" },
          { key: "Referrer-Policy", value: "strict-origin-when-cross-origin" },
          { key: "X-Frame-Options", value: "DENY" },
        ],
      },
    ];
  },
};

export default nextConfig;
