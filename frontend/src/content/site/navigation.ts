import { MODULES } from "./modules";
import { SOLUTIONS } from "./solutions";

export interface NavLink {
  label: string;
  href: string;
  description?: string;
}

export interface NavGroup {
  title: string;
  links: NavLink[];
}

export interface NavMenu {
  label: string;
  /** Direct link (no dropdown) when set. */
  href?: string;
  groups?: NavGroup[];
  /** Highlighted card in the mega menu. */
  feature?: { eyebrow: string; title: string; body: string; href: string; cta: string };
}

export const NAV: NavMenu[] = [
  { label: "Home", href: "/" },
  {
    label: "Solutions",
    groups: [
      {
        title: "By outcome",
        links: SOLUTIONS.map((s) => ({ label: s.name, href: `/solutions/${s.slug}`, description: s.short })),
      },
      {
        title: "Explore",
        links: [
          { label: "All solutions", href: "/solutions", description: "How BunonBrain supports each part of the plant." },
          { label: "Industries we serve", href: "/industries", description: "Knit, woven, denim, sweater and export houses." },
          { label: "Why BunonBrain", href: "/why-bunonbrain", description: "What makes the approach different." },
        ],
      },
    ],
    feature: {
      eyebrow: "Consultation",
      title: "Talk through your plant",
      body: "A working session on your lines, data and priorities. No obligation.",
      href: "/consultation",
      cta: "Book a consultation",
    },
  },
  {
    label: "Product",
    groups: [
      {
        title: "Platform",
        links: [
          { label: "Product overview", href: "/product", description: "The modules and how they fit together." },
          { label: "Features & capabilities", href: "/product/features", description: "Everything BunonBrain does today." },
          { label: "How it works", href: "/how-it-works", description: "From first data to daily decisions." },
        ],
      },
      {
        title: "Modules",
        links: MODULES.filter((m) => m.status !== "planned")
          .slice(0, 8)
          .map((m) => ({ label: m.name, href: `/product/${m.slug}` })),
      },
    ],
    feature: {
      eyebrow: "See it running",
      title: "Request a demo",
      body: "A guided walkthrough of BunonBrain on a simulated garment plant.",
      href: "/demo",
      cta: "Request a demo",
    },
  },
  { label: "Industries", href: "/industries" },
  {
    label: "Resources",
    groups: [
      {
        title: "Learn",
        links: [
          { label: "Resource center", href: "/resources", description: "Articles, guides and documentation in one place." },
          { label: "Blog", href: "/blog" },
          { label: "RMG industry insights", href: "/insights" },
          { label: "Guides", href: "/guides" },
          { label: "Case studies", href: "/case-studies" },
        ],
      },
      {
        title: "Support",
        links: [
          { label: "Help center", href: "/help" },
          { label: "Product documentation", href: "/docs" },
          { label: "FAQs", href: "/faq" },
          { label: "Contact support", href: "/support" },
          { label: "System status", href: "/status" },
        ],
      },
    ],
  },
  {
    label: "Company",
    groups: [
      {
        title: "About",
        links: [
          { label: "About BunonBrain", href: "/about" },
          { label: "Mission & vision", href: "/about/mission" },
          { label: "Our story", href: "/about/story" },
          { label: "Company overview", href: "/company" },
          { label: "Our values", href: "/company/values" },
        ],
      },
      {
        title: "People & news",
        links: [
          { label: "Leadership & team", href: "/company/leadership" },
          { label: "Culture", href: "/company/culture" },
          { label: "Careers", href: "/careers" },
          { label: "News & announcements", href: "/news" },
          { label: "Press & media", href: "/press" },
          { label: "Partnerships", href: "/partners" },
          { label: "Brand", href: "/brand" },
        ],
      },
    ],
  },
  { label: "Contact", href: "/contact" },
];

export const FOOTER: NavGroup[] = [
  {
    title: "Company",
    links: [
      { label: "About us", href: "/about" },
      { label: "Our mission", href: "/about/mission" },
      { label: "Careers", href: "/careers" },
      { label: "News", href: "/news" },
      { label: "Contact", href: "/contact" },
    ],
  },
  {
    title: "Solutions",
    links: [
      { label: "Workforce management", href: "/solutions/workforce-management" },
      { label: "Factory operations", href: "/solutions/factory-operations" },
      { label: "Production management", href: "/solutions/production-management" },
      { label: "Quality management", href: "/solutions/quality-management" },
      { label: "Analytics", href: "/solutions/analytics-intelligence" },
    ],
  },
  {
    title: "Resources",
    links: [
      { label: "Blog", href: "/blog" },
      { label: "Help center", href: "/help" },
      { label: "Documentation", href: "/docs" },
      { label: "FAQs", href: "/faq" },
      { label: "System status", href: "/status" },
    ],
  },
  {
    title: "Legal",
    links: [
      { label: "Privacy policy", href: "/legal/privacy-policy" },
      { label: "Terms of service", href: "/legal/terms-of-service" },
      { label: "Cookie policy", href: "/legal/cookie-policy" },
      { label: "Security policy", href: "/legal/security-policy" },
      { label: "All policies", href: "/legal" },
    ],
  },
];
