import type { MetadataRoute } from "next";

/** Web app manifest: installs as "BunonBrain" with the brand app icon. */
export default function manifest(): MetadataRoute.Manifest {
  return {
    name: "BunonBrain: Bangladesh RMG Workforce OS",
    short_name: "BunonBrain",
    description: "Smart Workforce. Stronger RMG. The operations layer for Bangladeshi garment factories.",
    start_url: "/app",
    display: "standalone",
    background_color: "#FFFFFF",
    theme_color: "#16324F",
    icons: [
      { src: "/brand/icon-192.png", sizes: "192x192", type: "image/png", purpose: "any" },
      { src: "/brand/icon-512.png", sizes: "512x512", type: "image/png", purpose: "any" },
      { src: "/brand/icon-maskable-512.png", sizes: "512x512", type: "image/png", purpose: "maskable" },
      { src: "/brand/bunonbrain-app-icon.svg", sizes: "any", type: "image/svg+xml", purpose: "any" },
    ],
  };
}
