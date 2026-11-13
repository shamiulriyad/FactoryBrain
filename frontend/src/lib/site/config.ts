import { TAGLINE } from "@/components/brand/logoPaths";

/**
 * Company facts used across the public website. Only verified information
 * belongs here: anything left null is simply not shown (no placeholder email,
 * phone, address or social profile is ever rendered).
 *
 * To publish contact details or social links, fill them in below; the footer,
 * Contact page and legal pages pick them up automatically.
 */
export const SITE = {
  name: "BunonBrain",
  tagline: TAGLINE,
  category: "Bangladesh RMG Workforce Operating System",
  description:
    "BunonBrain is the operations layer for Bangladesh's ready-made garment factories: production planning, line monitoring, quality inspection, merchandising, approvals and workforce records in one role-based system.",
  /** Public origin, used for canonical URLs, Open Graph and the sitemap. */
  url: (process.env.NEXT_PUBLIC_SITE_URL || "http://localhost:3000").replace(/\/$/, ""),

  contact: {
    email: null as string | null,
    phone: null as string | null,
    address: null as string | null,
  },

  /** e.g. { label: "LinkedIn", href: "https://www.linkedin.com/company/..." } */
  social: [] as { label: string; href: string }[],

  /**
   * Legal identity used by the policy pages. Until these are confirmed, the
   * policies show a highlighted "to be confirmed" marker in their place.
   */
  legal: {
    entityName: null as string | null,
    registeredAddress: null as string | null,
    governingLaw: null as string | null,
    privacyContact: null as string | null,
  },
} as const;

export const hasContactDetails = Boolean(SITE.contact.email || SITE.contact.phone || SITE.contact.address);
