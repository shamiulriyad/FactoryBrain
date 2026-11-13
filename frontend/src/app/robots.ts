import type { MetadataRoute } from "next";
import { SITE } from "@/lib/site/config";

export default function robots(): MetadataRoute.Robots {
  return {
    rules: [
      {
        userAgent: "*",
        allow: "/",
        // The signed-in application, onboarding, auth flows and the API are not for search engines.
        disallow: ["/app", "/api/", "/onboarding", "/dev", "/login", "/register", "/forgot-password", "/reset-password"],
      },
    ],
    sitemap: `${SITE.url}/sitemap.xml`,
    host: SITE.url,
  };
}
