"use client";

import { useEffect } from "react";
import Link from "next/link";
import { buttonClass } from "@/components/ui";
import { isChunkLoadFailure, reloadDocument } from "@/lib/chunk-recovery";

// Route-level error boundary. Without it, an uncaught error during a Server Component render
// surfaces as the opaque "This page couldn't load / A server error occurred" screen with only
// a numeric digest, which is impossible to act on. This renders a real message and the digest
// (for cross-referencing the server log), plus a retry that re-renders the segment.
export default function Error({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  const staleBuild = isChunkLoadFailure(error);

  useEffect(() => {
    // Surface the digest + message in the browser console so a user can report them, and in
    // the server log the same error is already recorded with this digest.
    console.error("Route error:", error);
  }, [error]);

  return (
    <div className="relative flex min-h-full flex-1 items-center justify-center overflow-hidden">
      <div className="blueprint absolute inset-0 opacity-50" />
      <div className="absolute inset-0 bg-gradient-to-b from-ink-950/40 to-ink-950" />

      <div className="relative px-5 text-center">
        <p className="font-mono text-5xl font-semibold text-bad-500">ERROR</p>
        <h1 className="mt-4 text-2xl font-semibold">
          {staleBuild ? "A new version of the site is available" : "Something went wrong on this page"}
        </h1>
        <p className="mx-auto mt-3 max-w-md text-sm leading-relaxed text-mist-400">
          {staleBuild
            ? "This tab is still running files from a previous release. Reloading fetches the current version."
            : "The page failed to load. You can retry, or head back to the dashboard. If it keeps happening, share the reference below with support so we can trace it in the server log."}
        </p>
        {!staleBuild && error.digest ? (
          <p className="mt-4 font-mono text-xs text-mist-500">Reference: {error.digest}</p>
        ) : null}
        <div className="mt-8 flex flex-wrap justify-center gap-3">
          <button
            type="button"
            onClick={staleBuild ? reloadDocument : reset}
            className={buttonClass("primary", "md")}
          >
            {staleBuild ? "Reload" : "Try again"}
          </button>
          <Link href="/dashboard" className={buttonClass("outline", "md")}>
            Go to dashboard
          </Link>
        </div>
      </div>
    </div>
  );
}
