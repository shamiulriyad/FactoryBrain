// Per-source input definitions for the onboarding connect step
// and the Integrations page modal. Returns the schema, validator and
// a default value generator (the "demo data" shortcut).

export type FieldDef = {
  key: string;
  label: string;
  type: "url" | "text" | "tel" | "file";
  placeholder?: string;
  required?: boolean;
  /** When type is "tel", a small prefix dropdown is shown. */
  countryCodes?: string[];
};

export type SourceForm = {
  id: string;
  title: string;
  fields: FieldDef[];
  /** Validate shape. Returns null when valid, otherwise an error message. */
  validate: (values: Record<string, string>) => string | null;
  /** Returns plausible dummy values for the "demo data" shortcut. */
  demoValues: () => Record<string, string>;
};

export const SOURCE_FORMS: Record<string, SourceForm> = {
  sheets: {
    id: "sheets",
    title: "Google Sheets",
    fields: [{ key: "url", label: "Sheet URL", type: "url", placeholder: "https://docs.google.com/spreadsheets/…", required: true }],
    validate: (v) => (isValidUrl(v.url) ? null : "Please enter a valid Google Sheets URL."),
    demoValues: () => ({ url: "https://docs.google.com/spreadsheets/d/demo-bunonbrain-products" })
  },
  shopify: {
    id: "shopify",
    title: "Shopify",
    fields: [
      { key: "domain", label: "Store domain", type: "text", placeholder: "yourshop.myshopify.com", required: true },
      { key: "token", label: "Access token (demo)", type: "text", placeholder: "shpat_•••••••••••", required: true }
    ],
    validate: (v) => {
      if (!v.domain || !/^[a-z0-9-]+\.myshopify\.com$/i.test(v.domain)) return "Domain should look like yourshop.myshopify.com";
      if (!v.token || v.token.length < 8) return "Token looks too short.";
      return null;
    },
    demoValues: () => ({ domain: "bunonbrain-demo.myshopify.com", token: "shpat_demoToken1234" })
  },
  whatsapp: {
    id: "whatsapp",
    title: "WhatsApp",
    fields: [
      { key: "country", label: "Country code", type: "text", placeholder: "+880", required: true, countryCodes: ["+880", "+91", "+1", "+44"] },
      { key: "phone", label: "Business number", type: "tel", placeholder: "1712345678", required: true }
    ],
    validate: (v) => {
      if (!v.country || !/^\+\d{1,4}$/.test(v.country)) return "Country code should start with + and contain digits.";
      const phone = (v.phone ?? "").replace(/[^0-9]/g, "");
      if (!phone || phone.length < 6 || phone.length > 14) return "Please enter a valid phone number.";
      return null;
    },
    demoValues: () => ({ country: "+880", phone: "1712345678" })
  },
  facebook: {
    id: "facebook",
    title: "Facebook",
    fields: [{ key: "url", label: "Page URL", type: "url", placeholder: "https://facebook.com/yourpage", required: true }],
    validate: (v) => (isValidUrl(v.url) ? null : "Please enter a valid URL."),
    demoValues: () => ({ url: "https://facebook.com/bunonbrain-demo" })
  },
  instagram: {
    id: "instagram",
    title: "Instagram",
    fields: [{ key: "handle", label: "Handle", type: "text", placeholder: "@yourbrand", required: true }],
    validate: (v) => (isValidHandle(v.handle) ? null : "Handles start with @ and use letters, numbers, dot or underscore."),
    demoValues: () => ({ handle: "@bunonbrain_demo" })
  },
  csv: {
    id: "csv",
    title: "CSV / Excel",
    fields: [
      { key: "file", label: "Upload file", type: "file", required: false },
      { key: "drive", label: "or paste a Drive link", type: "url", placeholder: "https://drive.google.com/…", required: false }
    ],
    validate: (v) => {
      if (!v.file && !v.drive) return "Pick a file or paste a Drive link.";
      if (v.drive && !isValidUrl(v.drive)) return "Drive link should be a URL.";
      return null;
    },
    demoValues: () => ({ file: "products-export.csv", drive: "" })
  },
  documents: {
    id: "documents",
    title: "Documents",
    fields: [{ key: "folder", label: "Drive folder link", type: "url", placeholder: "https://drive.google.com/drive/folders/…", required: true }],
    validate: (v) => (isValidUrl(v.folder) ? null : "Please enter a valid Drive folder URL."),
    demoValues: () => ({ folder: "https://drive.google.com/drive/folders/bunonbrain-policies-demo" })
  },
  // BunonBrain sensor feeds — synthetic, calibrated to NASA C-MAPSS, Kaggle
  // Bosch, and UCI SECOM public datasets. No real hardware required.
  "rfid-bundles": {
    id: "rfid-bundles",
    title: "RFID bundle scans",
    fields: [
      { key: "endpoint", label: "Reader endpoint", type: "url", placeholder: "https://rfid.factory.local", required: true },
      { key: "line", label: "Line scope", type: "text", placeholder: "all lines, or line-1,line-3", required: false }
    ],
    validate: (v) => (isValidUrl(v.endpoint) ? null : "Please enter a valid endpoint URL."),
    demoValues: () => ({ endpoint: "https://rfid.factory.local/demo", line: "all" })
  },
  "machine-telemetry": {
    id: "machine-telemetry",
    title: "Machine telemetry",
    fields: [
      { key: "endpoint", label: "Telemetry endpoint", type: "url", placeholder: "https://telemetry.factory.local", required: true },
      { key: "interval", label: "Sample interval (seconds)", type: "text", placeholder: "10", required: false }
    ],
    validate: (v) => {
      if (!isValidUrl(v.endpoint)) return "Please enter a valid endpoint URL.";
      if (v.interval && !/^\d+$/.test(v.interval)) return "Sample interval should be a number of seconds.";
      return null;
    },
    demoValues: () => ({ endpoint: "https://telemetry.factory.local/demo", interval: "10" })
  },
  "energy-meter": {
    id: "energy-meter",
    title: "Energy meter",
    fields: [
      { key: "endpoint", label: "Meter endpoint", type: "url", placeholder: "https://meter.factory.local", required: true },
      { key: "feed", label: "Feed", type: "text", placeholder: "factory-main", required: false }
    ],
    validate: (v) => (isValidUrl(v.endpoint) ? null : "Please enter a valid endpoint URL."),
    demoValues: () => ({ endpoint: "https://meter.factory.local/demo", feed: "factory-main" })
  }
};

export function listSourceIds(): string[] {
  return [
    "rfid-bundles",
    "machine-telemetry",
    "energy-meter",
    "sheets",
    "shopify",
    "whatsapp",
    "facebook",
    "instagram",
    "csv",
    "documents"
  ];
}

export function isValidUrl(s: string | undefined | null): boolean {
  if (!s) return false;
  try {
    const u = new URL(s);
    return u.protocol === "http:" || u.protocol === "https:";
  } catch {
    return false;
  }
}

export function isValidHandle(s: string | undefined | null): boolean {
  if (!s) return false;
  return /^@[a-zA-Z0-9._]{2,30}$/.test(s);
}

/** Masks a value for display on a CONNECTED card. */
export function maskValue(sourceId: string, value: string): string {
  if (!value) return "";
  if (sourceId === "shopify") {
    return value.split(".")[0] + ".myshopify.com · token••••";
  }
  if (sourceId === "whatsapp") {
    const m = value.match(/(\+\d+)(.*)/);
    if (m) return `${m[1]} •••• ${m[2].slice(-3)}`;
  }
  if (sourceId === "instagram") {
    return value.startsWith("@") ? value : "@" + value;
  }
  if (sourceId === "csv") {
    return value.length > 36 ? value.slice(0, 36) + "…" : value;
  }
  // URLs and folder links: truncate
  if (value.length > 36) return value.slice(0, 36) + "…";
  return value;
}