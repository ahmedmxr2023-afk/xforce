import http from "node:http";
import https from "node:https";
import { randomBytes } from "node:crypto";
import { Readable } from "node:stream";

// The engine answers a conversion only when it is finished, so the whole response — headers
// included — arrives after several minutes for a real program. Node's built-in fetch (undici)
// caps the wait for response headers at 300 s regardless of any AbortSignal, which surfaces as
// a bare "fetch failed" for longer runs. This helper posts the multipart form with node:http
// instead, socket timeouts disabled, honouring only the deadline the caller passes.

export interface EngineResponse {
  ok: boolean;
  status: number;
  json: unknown;
  raw: string;
}

export interface EngineBinaryResponse {
  ok: boolean;
  status: number;
  contentType: string | null;
  body: Buffer;
  /** Parsed JSON error body when the engine failed; null on success or unparseable. */
  json: unknown;
}

export class EngineTimeoutError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "TimeoutError";
  }
}

function escapeDisposition(value: string): string {
  return value.replace(/["\r\n]/g, "_");
}

/**
 * Turns any of the accepted body shapes into a readable stream of Buffers.
 *
 * Do not hand a Uint8Array to Readable.from() directly: it iterates a typed array byte by
 * byte, so the request receives numbers instead of chunks and Node fails the upload with
 * "The 'chunk' argument must be of type string or an instance of Buffer or Uint8Array"
 * (about two minutes later, once the socket has drained). Wrapping it in an array keeps the
 * payload as a single Buffer chunk.
 */
function bodySource(body: Uint8Array | ReadableStream<Uint8Array> | Readable): Readable {
  if (body instanceof Readable) return body;
  if (body instanceof Uint8Array) return Readable.from([Buffer.from(body)]);
  return Readable.fromWeb(body as Parameters<typeof Readable.fromWeb>[0]);
}

// Chunks the multipart body as a stream so a large upload is never held fully in memory.
async function* multipartParts(form: FormData, boundary: string): AsyncGenerator<Buffer> {
  for (const [name, value] of form.entries()) {
    if (typeof value === "string") {
      yield Buffer.from(
        `--${boundary}\r\nContent-Disposition: form-data; name="${escapeDisposition(name)}"\r\n\r\n` +
          `${value}\r\n`,
        "utf8",
      );
      continue;
    }

    const filename = escapeDisposition(value.name || "upload");
    yield Buffer.from(
      `--${boundary}\r\nContent-Disposition: form-data; name="${escapeDisposition(name)}"; ` +
        `filename="${filename}"\r\nContent-Type: ${value.type || "application/octet-stream"}\r\n\r\n`,
      "utf8",
    );

    const chunkSize = 64 * 1024;
    for (let offset = 0; offset < value.size; offset += chunkSize) {
      const slice = value.slice(offset, offset + chunkSize);
      yield Buffer.from(await slice.arrayBuffer());
    }
    yield Buffer.from("\r\n", "utf8");
  }
  yield Buffer.from(`--${boundary}--\r\n`, "utf8");
}

/**
 * Forwards an already-encoded request body straight to the engine without
 * re-parsing or re-encoding it. Use this when the caller has a raw body and its
 * content-type (e.g. proxying the browser's multipart upload): parsing it into a
 * FormData and rebuilding it is needlessly slow for large projects.
 */
export async function postEngineRaw(
  endpoint: string,
  body: Uint8Array | ReadableStream<Uint8Array> | Readable,
  contentType: string,
  options: { timeoutMs: number; headers?: Record<string, string> },
): Promise<EngineResponse> {
  const url = new URL(endpoint);
  const transport = url.protocol === "https:" ? https : http;

  const source = bodySource(body);

  return new Promise((resolve, reject) => {
    const request = transport.request(
      {
        protocol: url.protocol,
        hostname: url.hostname,
        port: url.port || (url.protocol === "https:" ? 443 : 80),
        path: `${url.pathname}${url.search}`,
        method: "POST",
        headers: { ...options.headers, "content-type": contentType },
      },
      (response) => {
        const parts: Buffer[] = [];
        response.on("data", (chunk: Buffer) => parts.push(chunk));
        response.on("end", () => {
          clearTimeout(deadline);
          const raw = Buffer.concat(parts).toString("utf8");
          let json: unknown = null;
          try {
            json = raw ? JSON.parse(raw) : null;
          } catch {
            json = null;
          }
          const status = response.statusCode ?? 500;
          resolve({ ok: status >= 200 && status < 300, status, json, raw });
        });
        response.on("error", (error) => {
          clearTimeout(deadline);
          reject(error);
        });
      },
    );

    request.setTimeout(0);

    const deadline = setTimeout(() => {
      request.destroy();
      reject(new EngineTimeoutError("the engine did not finish before the deadline"));
    }, options.timeoutMs);

    request.on("error", (error) => {
      clearTimeout(deadline);
      reject(error);
    });

    source.on("error", (error) => {
      clearTimeout(deadline);
      request.destroy();
      reject(error);
    });
    source.pipe(request);
  });
}

/**
 * Like {@link postEngine}, but keeps the response body as raw bytes so a binary
 * payload (e.g. the PDF from /api/pdf) survives intact. On failure the body is
 * still parsed as JSON so the caller can relay the engine's error message.
 */
export async function postEngineBytes(
  endpoint: string,
  body: Uint8Array | ReadableStream<Uint8Array> | Readable,
  contentType: string,
  options: { timeoutMs: number; headers?: Record<string, string> },
): Promise<EngineBinaryResponse> {
  const url = new URL(endpoint);
  const transport = url.protocol === "https:" ? https : http;

  const source = bodySource(body);

  return new Promise((resolve, reject) => {
    const request = transport.request(
      {
        protocol: url.protocol,
        hostname: url.hostname,
        port: url.port || (url.protocol === "https:" ? 443 : 80),
        path: `${url.pathname}${url.search}`,
        method: "POST",
        headers: { ...options.headers, "content-type": contentType },
      },
      (response) => {
        const parts: Buffer[] = [];
        response.on("data", (chunk: Buffer) => parts.push(chunk));
        response.on("end", () => {
          clearTimeout(deadline);
          const status = response.statusCode ?? 500;
          const ok = status >= 200 && status < 300;
          const buffer = Buffer.concat(parts);
          let json: unknown = null;
          if (!ok) {
            try {
              json = buffer.length ? JSON.parse(buffer.toString("utf8")) : null;
            } catch {
              json = null;
            }
          }
          resolve({
            ok,
            status,
            contentType: (response.headers["content-type"] as string | undefined) ?? null,
            body: buffer,
            json,
          });
        });
        response.on("error", (error) => {
          clearTimeout(deadline);
          reject(error);
        });
      },
    );

    request.setTimeout(0);

    const deadline = setTimeout(() => {
      request.destroy();
      reject(new EngineTimeoutError("the engine did not finish before the deadline"));
    }, options.timeoutMs);

    request.on("error", (error) => {
      clearTimeout(deadline);
      reject(error);
    });
    source.on("error", (error) => {
      clearTimeout(deadline);
      request.destroy();
      reject(error);
    });
    source.pipe(request);
  });
}

export async function postEngine(
  endpoint: string,
  form: FormData,
  options: { timeoutMs: number; headers?: Record<string, string> },
): Promise<EngineResponse> {
  const url = new URL(endpoint);
  const boundary = `----msw-${randomBytes(16).toString("hex")}`;
  const transport = url.protocol === "https:" ? https : http;

  return new Promise((resolve, reject) => {
    const request = transport.request(
      {
        protocol: url.protocol,
        hostname: url.hostname,
        port: url.port || (url.protocol === "https:" ? 443 : 80),
        path: `${url.pathname}${url.search}`,
        method: "POST",
        headers: {
          ...options.headers,
          "content-type": `multipart/form-data; boundary=${boundary}`,
        },
      },
      (response) => {
        const parts: Buffer[] = [];
        response.on("data", (chunk: Buffer) => parts.push(chunk));
        response.on("end", () => {
          clearTimeout(deadline);
          const raw = Buffer.concat(parts).toString("utf8");
          let json: unknown = null;
          try {
            json = raw ? JSON.parse(raw) : null;
          } catch {
            json = null;
          }
          const status = response.statusCode ?? 500;
          resolve({ ok: status >= 200 && status < 300, status, json, raw });
        });
        response.on("error", (error) => {
          clearTimeout(deadline);
          reject(error);
        });
      },
    );

    // No socket idle timeout: the caller's deadline is the only limit.
    request.setTimeout(0);

    const deadline = setTimeout(() => {
      request.destroy();
      reject(new EngineTimeoutError("the engine did not finish before the deadline"));
    }, options.timeoutMs);

    request.on("error", (error) => {
      clearTimeout(deadline);
      reject(error);
    });

    Readable.from(multipartParts(form, boundary)).pipe(request);
  });
}
