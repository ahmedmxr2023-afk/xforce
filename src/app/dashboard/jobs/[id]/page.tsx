import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { getCurrentUser } from "@/lib/auth";
import { getJobForUser } from "@/lib/jobs";
import { userHasFeature } from "@/lib/licensing";
import { PageTitle } from "@/components/dashboard/page-title";
import { JobStatusBadge } from "@/components/dashboard/job-bits";
import { JobAutoRefresh } from "@/components/dashboard/job-auto-refresh";
import { ConvertPipeline } from "@/components/engine/convert-flow";
import { sourceKindForFile, type TargetKind } from "@/lib/convert-flow";
import { ConvertStations } from "@/components/nx/convert-stations";
import { Badge, ButtonLink, Panel, PanelTitle, Stat, cx } from "@/components/ui";
import { formatBytes, formatDateTime } from "@/lib/util";

const TARGET_LABELS: Record<string, string> = {
  tia: "TIA package",
  pt2x: "PT2X",
  xml: "TriStation XML",
  msw: "TIA package",
};

function targetKindOf(raw: string): TargetKind {
  return raw === "pt2x" || raw === "xml" ? raw : "tia";
}

const SOURCE_NOUN: Record<string, string> = {
  pdf: "PDF",
  lpt: "listing",
  xml: "XML",
  pt2: "PT2X",
  ap: "TIA",
};

function pt2xOutputIndex(outputs: { name: string }[]): number {
  return outputs.findIndex((output) => /\.pt2x?$/i.test(output.name));
}

export default async function JobDetailPage({ params }: PageProps<"/dashboard/jobs/[id]">) {
  const user = await getCurrentUser();
  if (!user) redirect("/login");

  const { id } = await params;
  const job = getJobForUser(id, user.id);
  if (!job) notFound();

  const running = job.status === "processing" || job.status === "queued";
  const targetRaw = typeof job.options.target === "string" ? job.options.target : "tia";
  const targetLabel = TARGET_LABELS[targetRaw] ?? targetRaw;
  const targetKind = targetKindOf(targetRaw);
  const sourceKind = sourceKindForFile(job.source_name);
  const sourceNoun = SOURCE_NOUN[sourceKind] ?? "source";
  const canView = userHasFeature(user.id, "viewer");
  const viewerIndex = job.status === "completed" ? pt2xOutputIndex(job.outputs) : -1;

  return (
    <div className="space-y-8">
      <JobAutoRefresh active={running} />

      <PageTitle
        title={job.source_name}
        description={`Conversion job ${job.id} · ${formatBytes(job.source_size)} · started ${formatDateTime(job.started_at ?? job.created_at)}`}
        action={
          <div className="flex gap-2">
            <ButtonLink href="/dashboard/jobs" variant="outline" size="sm">
              All jobs
            </ButtonLink>
            <ButtonLink href="/dashboard/convert" size="sm">
              New conversion
            </ButtonLink>
          </div>
        }
      />

      {/* ---- Status ---- */}
      <Panel className="p-5">
        <div className="flex flex-wrap items-center gap-x-6 gap-y-3">
          <div className="flex items-center gap-3">
            <JobStatusBadge status={job.status} />
            {running ? (
              <span className="text-xs text-mist-500">This page refreshes itself while the job runs.</span>
            ) : null}
          </div>
          <div className="ml-auto flex flex-wrap items-center gap-x-6 gap-y-2 text-xs text-mist-400">
            <span>
              Output: <span className="font-medium text-mist-200">{targetLabel}</span>
            </span>
            <span>
              Engine: <span className="font-mono">{job.engine}</span>
            </span>
            {job.finished_at ? <span>Finished {formatDateTime(job.finished_at)}</span> : null}
          </div>
        </div>

        {running ? (
          <div className="mt-4 h-1.5 overflow-hidden rounded-full bg-ink-800">
            <div className="h-full w-1/3 animate-pulse rounded-full bg-gradient-to-r from-signal-600 to-signal-400" />
          </div>
        ) : null}

        {job.error ? (
          <p
            role="alert"
            className="mt-4 rounded-lg border border-bad-500/40 bg-bad-500/10 px-3.5 py-2.5 text-xs leading-relaxed text-bad-500"
          >
            {job.error}
          </p>
        ) : null}

        {/* ---- Live conversion path ---- */}
        {job.status !== "failed" ? (
          <div className="mt-4 space-y-3 border-t border-ink-800 pt-4">
            <ConvertPipeline
              sourceKind={sourceKind}
              target={targetKind}
              status={running ? "busy" : job.status === "completed" ? "done" : "idle"}
              sourceName={job.source_name}
            />
            <ConvertStations active={running} stage={job.status === "completed" ? "done" : null} />
          </div>
        ) : null}
      </Panel>

      {/* ---- Success banner ---- */}
      {job.status === "completed" ? (
        <div className="rounded-xl border-2 border-signal-500/50 bg-signal-500/10 p-6">
          <div className="flex items-start gap-4">
            <span
              className="grid h-12 w-12 shrink-0 place-items-center rounded-xl border-2 border-signal-500/60 bg-signal-500/20 text-signal-300"
              aria-hidden="true"
            >
              <svg viewBox="0 0 24 24" className="h-6 w-6" fill="none" stroke="currentColor" strokeWidth={2.5}>
                <path d="m5 13 4 4 10-11" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
            </span>
            <div className="min-w-0 flex-1">
              <p className="text-base font-bold text-mist-100">
                Your {sourceNoun} file has successfully converted to {targetLabel}.
              </p>
              <p className="mt-1 text-xs text-mist-400">
                {job.outputs.length === 1 ? "1 file is ready" : `${job.outputs.length} files are ready`} to download below
                {job.finished_at ? ` · finished ${formatDateTime(job.finished_at)}` : ""}.
              </p>
              {canView && viewerIndex >= 0 ? (
                <div className="mt-4 flex flex-wrap items-center gap-2">
                  <Link
                    href={`/dashboard/viewer?job=${job.id}&index=${viewerIndex}`}
                    className="inline-flex items-center gap-2 rounded-lg border border-signal-500/60 bg-signal-500/10 px-3.5 py-2 text-xs font-semibold text-signal-200 transition-colors hover:bg-signal-500/20"
                  >
                    <svg viewBox="0 0 24 24" className="h-3.5 w-3.5" fill="none" stroke="currentColor" strokeWidth={2}>
                      <path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7Z" strokeLinecap="round" strokeLinejoin="round" />
                      <circle cx="12" cy="12" r="3" />
                    </svg>
                    Open in viewer
                  </Link>
                  <a
                    href={`/api/jobs/${job.id}/output/${viewerIndex}/pdf`}
                    className="inline-flex items-center gap-2 rounded-lg border border-ink-600 px-3.5 py-2 text-xs font-semibold text-mist-200 transition-colors hover:border-signal-500/60 hover:text-mist-100"
                  >
                    <svg viewBox="0 0 24 24" className="h-3.5 w-3.5" fill="none" stroke="currentColor" strokeWidth={2}>
                      <path d="M12 4v12m0 0l-4-4m4 4l4-4" strokeLinecap="round" strokeLinejoin="round" />
                      <path d="M4 18v1a2 2 0 002 2h12a2 2 0 002-2v-1" strokeLinecap="round" />
                    </svg>
                    Download FBD as PDF
                  </a>
                  <a
                    href={`/api/jobs/${job.id}/output/${viewerIndex}/pdf?mode=reports`}
                    className="inline-flex items-center gap-2 rounded-lg border border-ink-600 px-3.5 py-2 text-xs font-semibold text-mist-200 transition-colors hover:border-signal-500/60 hover:text-mist-100"
                  >
                    <svg viewBox="0 0 24 24" className="h-3.5 w-3.5" fill="none" stroke="currentColor" strokeWidth={2}>
                      <path d="M7 3h7l4 4v14H7z" strokeLinecap="round" strokeLinejoin="round" />
                      <path d="M14 3v4h4" strokeLinecap="round" strokeLinejoin="round" />
                    </svg>
                    Download reports as PDF
                  </a>
                </div>
              ) : null}
              {targetRaw === "pt2x" ? (
                <div className="mt-4">
                  <Link
                    href="/guides/tristation"
                    className="inline-flex items-center gap-2 text-xs font-semibold text-signal-300 underline-offset-4 hover:underline"
                  >
                    How to open this PT2X in TriStation
                  </Link>
                </div>
              ) : null}
            </div>
          </div>
        </div>
      ) : null}

      {/* ---- Outputs ---- */}
      {job.status === "completed" ? (
        <Panel>
          <PanelTitle
            title="Downloads"
            description={job.outputs.length === 1 ? "1 file" : `${job.outputs.length} files`}
          />
          <ul className="divide-y divide-ink-800">
            {job.outputs.map((output, index) => (
              <li key={output.name} className="flex items-center gap-4 px-5 py-3">
                <span
                  className={cx(
                    "grid h-8 w-8 shrink-0 place-items-center rounded-md border",
                    output.kind === "primary"
                      ? "border-signal-500/40 bg-signal-500/10 text-signal-300"
                      : "border-ink-700 bg-ink-900/60 text-mist-500",
                  )}
                  aria-hidden="true"
                >
                  {output.kind === "primary" ? (
                    <svg viewBox="0 0 24 24" className="h-4 w-4" fill="none" stroke="currentColor" strokeWidth={1.5}>
                      <path d="M12 4v12m0 0l-4-4m4 4l4-4" strokeLinecap="round" strokeLinejoin="round" />
                      <path d="M4 18v1a2 2 0 002 2h12a2 2 0 002-2v-1" strokeLinecap="round" />
                    </svg>
                  ) : (
                    <svg viewBox="0 0 24 24" className="h-4 w-4" fill="none" stroke="currentColor" strokeWidth={1.5}>
                      <path d="M7 3h7l4 4v14H7z" />
                      <path d="M14 3v4h4" />
                    </svg>
                  )}
                </span>
                <div className="min-w-0 flex-1">
                  <p className="truncate font-mono text-xs text-mist-200">{output.name}</p>
                  <p className="mt-0.5 text-[11px] text-mist-500">{formatBytes(output.size)}</p>
                </div>
                <Badge tone={output.kind === "primary" ? "good" : "neutral"}>{output.kind}</Badge>
                <a
                  href={`/api/jobs/${job.id}/output/${index}`}
                  className="shrink-0 rounded-lg border border-ink-600 px-3 py-1.5 text-xs font-medium text-mist-200 transition-colors hover:border-signal-500/60 hover:text-mist-100"
                >
                  Download
                </a>
              </li>
            ))}
          </ul>
        </Panel>
      ) : null}

      {/* ---- Warnings ---- */}
      {job.warnings.length > 0 ? (
        <Panel>
          <PanelTitle title={`Warnings (${job.warnings.length})`} description="Non-fatal issues the engine reported." />
          <ul className="space-y-2 px-5 py-4">
            {job.warnings.map((warning, index) => (
              <li key={index} className="flex gap-2.5 text-xs leading-relaxed text-mist-400">
                <span aria-hidden="true" className="mt-1 h-1.5 w-1.5 shrink-0 rounded-full bg-amberline-400" />
                {warning}
              </li>
            ))}
          </ul>
        </Panel>
      ) : null}

      {/* ---- Detail ---- */}
      <div className="grid gap-6 sm:grid-cols-2 lg:grid-cols-4">
        <Stat label="Source size" value={formatBytes(job.source_size)} />
        <Stat label="Progress" value={job.status === "completed" ? "100%" : running ? `${job.progress}%` : job.status === "failed" ? "Failed" : `${job.progress}%`} />
        <Stat label="Created" value={formatDateTime(job.created_at)} />
        <Stat label="Finished" value={job.finished_at ? formatDateTime(job.finished_at) : "—"} />
      </div>

      {Object.keys(job.options).length > 0 ? (
        <Panel>
          <PanelTitle title="Options" description="The settings this job ran with." />
          <dl className="grid gap-x-8 gap-y-3 px-5 py-4 sm:grid-cols-2">
            {Object.entries(job.options).map(([key, value]) => (
              <div key={key} className="flex items-baseline justify-between gap-4 text-xs">
                <dt className="shrink-0 text-mist-500">{key}</dt>
                <dd className="truncate font-mono text-mist-300" title={String(value)}>
                  {value === null ? "—" : String(value)}
                </dd>
              </div>
            ))}
          </dl>
        </Panel>
      ) : null}

      {job.status === "failed" ? (
        <p className="text-xs text-mist-500">
          Fixed the source?{" "}
          <Link href="/dashboard/convert" className="text-signal-300 underline-offset-4 hover:underline">
            Start a new conversion
          </Link>
          .
        </p>
      ) : null}
    </div>
  );
}
