export interface NavItem {
  href: string;
  label: string;
}

export const NAV_ITEMS: NavItem[] = [
  { href: "/features", label: "Features" },
  { href: "/how-it-works", label: "How it works" },
  { href: "/portfolio", label: "Capabilities" },
  { href: "/pricing", label: "Pricing" },
  { href: "/contact", label: "Contact" },
];

export const PRODUCT_LINKS: NavItem[] = [
  { href: "/dashboard", label: "Dashboard" },
  { href: "/dashboard/jobs", label: "Job history" },
  { href: "/dashboard/licenses", label: "Licenses" },
  { href: "/dashboard/billing", label: "Billing" },
  { href: "/guides/tristation", label: "Open in TriStation" },
];
