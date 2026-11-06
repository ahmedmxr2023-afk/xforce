"use client";

import { isChunkLoadFailure, reloadDocument } from "@/lib/chunk-recovery";

// Last-resort boundary: replaces the root layout when the layout itself throws. Because it
// stands in for layout.tsx it must render its own <html>/<body>, and it cannot rely on the
// app stylesheet, so the styling here is intentionally inline and self-contained.
export default function GlobalError({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  const staleBuild = isChunkLoadFailure(error);

  return (
    <html lang="en">
      <body
        style={{
          margin: 0,
          minHeight: "100vh",
          display: "flex",
          alignItems: "center",
          justifyContent: "center",
          background: "#0b0f14",
          color: "#e6edf3",
          fontFamily: "system-ui, -apple-system, Segoe UI, Roboto, sans-serif",
          textAlign: "center",
          padding: "1.5rem",
        }}
      >
        <div>
          <p style={{ margin: 0, fontFamily: "monospace", fontSize: "2.5rem", fontWeight: 600, color: "#ff6b6b" }}>
            ERROR
          </p>
          <h1 style={{ marginTop: "0.75rem", fontSize: "1.35rem", fontWeight: 600 }}>
            {staleBuild ? "A new version of the site is available" : "The application failed to load"}
          </h1>
          <p style={{ margin: "0.75rem auto 0", maxWidth: "28rem", fontSize: "0.875rem", lineHeight: 1.6, color: "#9aa7b4" }}>
            {staleBuild
              ? "This tab is still running files from a previous release. Reloading fetches the current version."
              : "A server error occurred before the page could render. Try again; if it persists, share the reference below with support."}
          </p>
          {!staleBuild && error.digest ? (
            <p style={{ marginTop: "1rem", fontFamily: "monospace", fontSize: "0.75rem", color: "#6b7a89" }}>
              Reference: {error.digest}
            </p>
          ) : null}
          <button
            type="button"
            onClick={staleBuild ? reloadDocument : reset}
            style={{
              marginTop: "1.5rem",
              cursor: "pointer",
              border: "1px solid #2f81f7",
              background: "#1f6feb",
              color: "#fff",
              fontSize: "0.875rem",
              fontWeight: 600,
              padding: "0.5rem 1rem",
              borderRadius: "0.5rem",
            }}
          >
            {staleBuild ? "Reload" : "Try again"}
          </button>
        </div>
      </body>
    </html>
  );
}
