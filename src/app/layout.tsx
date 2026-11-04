import type { Metadata, Viewport } from "next";
import { Geist, Geist_Mono } from "next/font/google";
import { appUrl, siteName } from "@/lib/env";
import "./globals.css";

const geistSans = Geist({ variable: "--font-geist-sans", subsets: ["latin"] });
const geistMono = Geist_Mono({ variable: "--font-geist-mono", subsets: ["latin"] });

const title = siteName();

/** Official wordmark, served unmodified from /brand. */
const brandImage = {
  url: "/brand/xforcemsw-logo.png",
  width: 1983,
  height: 793,
  alt: `${title} — industrial logic, reconstructed`,
};

export const metadata: Metadata = {
  metadataBase: appUrl() ? new URL(appUrl()) : undefined,
  title: {
    default: `${title} — Industrial logic, reconstructed`,
    template: `%s · ${title}`,
  },
  description:
    "XFORCEMSW is engineering software for industrial control logic. It reads text-based ladder PDFs and ASCII listings, reconstructs their structure, generates Boolean equations, cross-references signals, reports analysis findings, and exports engineering artefacts — deterministically and offline.",
  applicationName: title,
  keywords: [
    "ladder diagram",
    "PLC",
    "ladder logic parsing",
    "Boolean equations",
    "logic cross-reference",
    "TRISTATION",
    "Triconex",
    "MSW",
    "controls engineering",
    "logic documentation",
    "PLC modernization",
  ],
  openGraph: {
    title: `${title} — Industrial logic, reconstructed`,
    description:
      "Read industrial control logic from PDFs and listings, reconstruct its structure, cross-reference signals, analyse it, and export engineering artefacts from one source.",
    type: "website",
    siteName: title,
    images: [brandImage],
  },
  twitter: {
    card: "summary_large_image",
    title: `${title} — Industrial logic, reconstructed`,
    description:
      "Read industrial control logic from PDFs and listings, reconstruct its structure, cross-reference signals, analyse it, and export engineering artefacts from one source.",
    images: [brandImage.url],
  },
  robots: { index: true, follow: true },
};

export const viewport: Viewport = {
  themeColor: "#05080f",
  colorScheme: "dark",
};

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    // The landing pages arm their scroll reveals from an inline script that runs before the
    // first paint (RevealBootstrap sets data-reveal on <html>), so the DOM intentionally has
    // an attribute the server markup cannot know about. suppressHydrationWarning only covers
    // this element's own attributes; children are still checked. data-scroll-behavior lets the
    // router suspend the smooth scrolling that globals.css switches on while it resets scroll.
    <html
      lang="en"
      className={`${geistSans.variable} ${geistMono.variable} h-full antialiased`}
      data-scroll-behavior="smooth"
      suppressHydrationWarning
    >
      <body className="min-h-full flex flex-col bg-ink-950 text-mist-200">{children}</body>
    </html>
  );
}
