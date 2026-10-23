/** @type {import('next').NextConfig} */
const nextConfig = {
  reactStrictMode: true,
  experimental: {
    optimizePackageImports: ["lucide-react", "recharts"]
  },

  // ── Frontend ↔ ASP.NET Core bridge ──────────────────────────────────────────
  // Every /api/* request from client components is transparently proxied to
  // the .NET 9 backend. NO frontend code change is required.
  //
  // Override the backend URL via NEXT_PUBLIC_API_URL (preferred) or
  // DOTNET_API_URL (legacy fallback). Default: http://localhost:5000.
  //
  // Legacy Next.js App Router route handlers that previously resolved /api/*
  // live under src/_legacy_api/** (outside src/app, so Next.js never mounts
  // them as routes regardless of the leading underscore).
  //
  // Exception: three route handlers stay mounted under src/app/api/**
  // (settings/llm, rag/ingest, rag/reindex). Next.js resolves filesystem
  // routes before a plain-array rewrite, so these paths hit the local
  // handler first. That handler is a server-side proxy (src/lib/adminProxy.ts)
  // which forwards to the same .NET controller and attaches the server-only
  // ADMIN_API_TOKEN as X-Admin-Token — something the browser can't do
  // through this rewrite. The .NET backend remains the owner of these routes.
  async rewrites() {
    const target =
      process.env.NEXT_PUBLIC_API_URL ||
      process.env.DOTNET_API_URL ||
      'http://localhost:5000';
    return [
      {
        source: '/api/:path*',
        destination: `${target}/api/:path*`,
      },
    ];
  },
  // `src/services/rag/vector-store.ts` (and friends) lazy-resolves
  // `node:fs` / `node:path` / `node:crypto` only on the server. The RAG
  // index is never instantiated on the client, so the browser bundle
  // shouldn't try to resolve the node: schemes. We tell webpack to leave
  // them as external imports — at runtime the calls happen only on the
  // server where Node.js supplies the modules naturally.
  webpack: (config, { isServer }) => {
    if (!isServer) {
      // Client bundle: externalize any node: scheme imports so webpack
      // doesn't try to bundle them. They're never invoked client-side.
      const externals = Array.isArray(config.externals) ? config.externals : [config.externals];
      externals.push(({ request }, callback) => {
        if (typeof request === "string" && request.startsWith("node:")) {
          return callback(null, "commonjs " + request);
        }
        callback();
      });
      config.externals = externals;
    }
    return config;
  }
};

export default nextConfig;