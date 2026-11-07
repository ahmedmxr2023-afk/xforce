import { NextResponse } from "next/server";
import { getCurrentUser } from "@/lib/auth";
import { getDashboardContext } from "@/lib/dashboard";
import { createAndRunJob, QuotaError } from "@/lib/jobs";
import {
  checkLicenseKey,
  claimSeat,
  featureDeniedMessage,
  grantedFeatures,
  grantedFeaturesForKey,
  sourceFeature,
  targetFeature,
  type ConversionTarget,
} from "@/lib/licensing";
import type { LicenseFeature } from "@/lib/types";
import { clientKey, rateLimit } from "@/lib/rate-limit";
import { DEFAULT_OPTIONS, type ConversionOptions } from "@/lib/converter";
import { clean } from "@/lib/validate";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

// The conversion engine reads several source families: ladder PDFs and TRISTATION listings
// (.pdf/.lpt/.txt), TriStation XML exports, raw TriStation project containers (.pt2/.pt2x),
// and Siemens SimaticML exports (.zip or a single block .xml, plus .ap16–.ap20 shorthands).
// The engine sniffs contents, so the list stays aligned with its PrepareSourceAsync.
const ALLOWED_EXTENSIONS = [
  ".pdf", ".txt", ".lpt", ".lst", ".log", ".csv",
  ".xml", ".zip", ".pt2", ".pt2x",
  ".ap16", ".ap17", ".ap18", ".ap19", ".ap20",
];

function extensionOf(name: string): string {
  const index = name.lastIndexOf(".");
  return index === -1 ? "" : name.slice(index).toLowerCase();
}

export async function POST(request: Request) {
  const user = await getCurrentUser();
  if (!user) return NextResponse.json({ error: "Sign in to run conversions." }, { status: 401 });

  const limit = rateLimit(clientKey(["job", user.id]), 60, 60 * 60_000);
  if (!limit.ok) {
    return NextResponse.json(
      { error: "Too many conversion requests. Please slow down." },
      { status: 429 },
    );
  }

  let form: FormData;
  try {
    form = await request.formData();
  } catch (error) {
    // A truncated multipart body (proxy body cap, interrupted upload) lands here. Log the
    // real cause so it is not silently reported as a generic client error.
    console.error("[/api/jobs] multipart parse failed", {
      contentType: request.headers.get("content-type"),
      contentLength: request.headers.get("content-length"),
      error: error instanceof Error ? error.message : String(error),
    });
    return NextResponse.json({ error: "Malformed upload." }, { status: 400 });
  }

  const file = form.get("file");
  if (!(file instanceof File)) {
    return NextResponse.json({ error: "Choose a source file to upload." }, { status: 400 });
  }

  if (!ALLOWED_EXTENSIONS.includes(extensionOf(file.name))) {
    return NextResponse.json(
      { error: `Unsupported file type. Accepted: ${ALLOWED_EXTENSIONS.join(", ")}.` },
      { status: 400 },
    );
  }

  const context = await getDashboardContext(user);

  const rawKey = clean(form.get("licenseKey"), 80);
  let licenseId: string | null = null;
  let licenseKey: string | null = null;

  if (rawKey) {
    const check = checkLicenseKey(rawKey);
    if (!check.valid || !check.license) {
      return NextResponse.json(
        { error: `License key rejected: ${check.reason ?? "invalid"}.` },
        { status: 400 },
      );
    }
    // A license is shareable up to its seat allowance: the first N distinct
    // accounts to use the key each hold a seat, and reuse does not consume more.
    const seat = claimSeat(check.license.id, user.email, "account", user.id);
    if (!seat.ok) {
      return NextResponse.json(
        {
          error: `This license has no free seats left (${seat.seatsUsed}/${seat.seatLimit} in use). Ask the license owner or an administrator to raise the seat count.`,
        },
        { status: 403 },
      );
    }
    licenseId = check.license.id;
    licenseKey = check.license.key;
  } else {
    const firstActive = context.licenses.find(
      (license) => license.status === "active" && license.activations < license.activation_limit,
    );
    if (firstActive) {
      const seat = claimSeat(firstActive.id, user.email, "account", user.id);
      if (seat.ok) {
        licenseId = firstActive.id;
        licenseKey = firstActive.key;
      }
    }
  }

  const rawTarget = clean(form.get("target"), 10);
  const target: ConversionTarget = rawTarget === "xml" || rawTarget === "pt2x" || rawTarget === "tia" ? rawTarget : "tia";
  const options: ConversionOptions = {
    ...DEFAULT_OPTIONS,
    targetFormat: clean(form.get("targetFormat"), 20) || DEFAULT_OPTIONS.targetFormat,
    // "tia" (the import package) is the default: it is what every job returned before the
    // target field existed, so omitting it keeps old clients working.
    target,
    includeNetlist: form.get("includeNetlist") === "on",
    includeBom: form.get("includeBom") === "on",
    includeRungsReport: form.get("includeRungsReport") === "on",
    sheetName: clean(form.get("sheetName"), 60) || null,
    notes: clean(form.get("notes"), 500) || null,
  };

  // Capability gate. Features come from the specific key when one is supplied,
  // otherwise from the union of the account's active licenses. Nothing here is
  // reachable without the matching grant.
  const available: LicenseFeature[] = rawKey
    ? grantedFeaturesForKey(rawKey)
    : grantedFeatures(user.id);

  const required: LicenseFeature[] = [targetFeature(target)];
  const srcFeature = sourceFeature(file.name);
  if (srcFeature) required.push(srcFeature);
  if (options.includeNetlist) required.push("artifacts.netlist");
  if (options.includeBom) required.push("artifacts.bom");
  if (options.includeRungsReport) required.push("artifacts.rungs_report");

  const missing = required.filter((feature) => !available.includes(feature));
  if (missing.length > 0) {
    return NextResponse.json({ error: featureDeniedMessage(missing), code: "feature_denied" }, { status: 403 });
  }

  try {
    const job = await createAndRunJob({
      user,
      licenseId,
      licenseKey,
      upload: {
        filename: file.name,
        mimeType: file.type,
        bytes: Buffer.from(await file.arrayBuffer()),
      },
      options,
      maxUploadBytes: context.plan.maxUploadBytes,
      monthlyConversions: context.plan.monthlyConversions,
      usedThisMonth: context.usedThisMonth,
      retentionDays: context.plan.retentionDays,
    });

    return NextResponse.json({ id: job.id, status: job.status, error: job.error }, { status: 201 });
  } catch (error) {
    if (error instanceof QuotaError) {
      return NextResponse.json({ error: error.message }, { status: 402 });
    }
    return NextResponse.json(
      { error: error instanceof Error ? error.message : "Conversion failed." },
      { status: 500 },
    );
  }
}
