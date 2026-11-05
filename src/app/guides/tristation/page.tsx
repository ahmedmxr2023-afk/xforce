import type { Metadata } from "next";
import Link from "next/link";
import { MarketingShell, PageHeader } from "@/components/marketing-shell";
import { ButtonLink, Panel, PanelTitle } from "@/components/ui";

export const metadata: Metadata = {
  title: "Open a converted PT2X in TriStation",
  description:
    "Step-by-step guide to opening an XforceMsw PT2X deliverable in TriStation 1131 5.8, including the first-open sidecar alerts.",
};

const STEPS = [
  {
    title: "Download the PT2X",
    body: "Finish a conversion with the PT2X target and download the primary .pt2x file from the job page. Keep the downloaded file in a normal folder — do not rename it while TriStation has it open.",
  },
  {
    title: "Open it in TriStation 1131 5.8",
    body: "Start TriStation, choose File → Open Project, and select the .pt2x file. The first open takes a minute while TriStation reads the catalogue and drawings.",
  },
  {
    title: "Acknowledge the two sidecar alerts",
    body: "WB0565 (project properties .DAT missing) and WB0442 (audit trail missing) are normal for a container produced outside TriStation. Click OK on both: TriStation creates those sidecar files next to the project and continues.",
  },
  {
    title: "Check the drawings",
    body: "Programs and function blocks are in the Application Workspace. Blocks show their pin names, sheet templates are the installed STDSHEET set, and instances are not marked out of date.",
  },
];

export default function TriStationGuidePage() {
  return (
    <MarketingShell>
      <PageHeader
        eyebrow="Guide"
        title="Open a converted PT2X in TriStation"
        description="The deliverable is a complete TriStation project container built by our own converter. You only need TriStation to view or edit it — never to produce it."
      />

      <section className="mx-auto max-w-6xl px-5 py-16">
        <div className="grid gap-8 lg:grid-cols-[1.4fr_1fr]">
          <div className="space-y-6">
            {STEPS.map((step, index) => (
              <Panel key={step.title} className="p-6">
                <div className="flex items-start gap-4">
                  <span className="grid h-8 w-8 shrink-0 place-items-center rounded-lg border border-signal-500/40 bg-signal-500/10 font-mono text-xs font-semibold text-signal-300">
                    {index + 1}
                  </span>
                  <div>
                    <h2 className="text-sm font-semibold text-mist-100">{step.title}</h2>
                    <p className="mt-2 text-xs leading-relaxed text-mist-400">{step.body}</p>
                  </div>
                </div>
              </Panel>
            ))}
          </div>

          <div className="space-y-6">
            <Panel>
              <PanelTitle title="What the file already contains" />
              <ul className="space-y-2.5 px-5 py-4 text-xs leading-relaxed text-mist-400">
                <li>· Programs, function blocks and program-defined FB interfaces</li>
                <li>· Tags, aliases, scaling and hardware allocation from the export</li>
                <li>· Installed 5.8 library identities and sheet templates</li>
                <li>· FBD drawings, wires, comments and dividers</li>
              </ul>
            </Panel>

            <Panel>
              <PanelTitle title="If you plan to build it" />
              <p className="px-5 py-4 text-xs leading-relaxed text-mist-400">
                Building requires a licensed TriStation installation. Some PLCopen exports are
                lossy: missing execution lists, duplicate addresses or omitted literal constants
                are reported in the conversion report and by TriStation at build time. They are
                source defects, not conversion defects.
              </p>
            </Panel>

            <Panel>
              <PanelTitle title="Never copy a file after a failed open" />
              <p className="px-5 py-4 text-xs leading-relaxed text-mist-400">
                When TriStation attempts to open a container it may pad the file on disk. If an open
                fails, download a fresh copy instead of reusing the modified one.
              </p>
            </Panel>

            <div className="flex flex-wrap gap-3">
              <ButtonLink href="/dashboard/convert">Start a conversion</ButtonLink>
              <Link
                href="/features"
                className="inline-flex items-center rounded-lg border border-ink-600 px-4 py-2 text-xs font-semibold text-mist-200 transition-colors hover:border-signal-500/60 hover:text-mist-100"
              >
                What the converter supports
              </Link>
            </div>
          </div>
        </div>
      </section>
    </MarketingShell>
  );
}
