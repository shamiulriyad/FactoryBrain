import path from 'node:path';
import { fileURLToPath } from 'node:url';
import nextEnv from '@next/env';

// One .env for the whole repo: load the repo-root .env* files (one level up
// from frontend/) instead of frontend/.env*. Next.js has already called
// loadEnvConfig(frontend/) and cached it by the time this file runs, so
// forceReload (4th arg) is needed. Values in the real process env still win.
//
// FACTORYBRAIN_SKIP_ROOT_ENV=1 skips the repo-root .env entirely. The
// end-to-end harness (e2e/run-e2e.mjs) sets it so test builds are configured
// only by the variables it passes, never by a developer's real .env.
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
if (process.env.FACTORYBRAIN_SKIP_ROOT_ENV !== '1') {
  nextEnv.loadEnvConfig(repoRoot, process.env.NODE_ENV !== 'production', undefined, true);
}

/** @type {import('next').NextConfig} */
const nextConfig = {
  reactStrictMode: true,
  // NEXT_DIST_DIR lets the e2e harness build into its own (git-ignored)
  // folder instead of overwriting the .next a running `next start` serves.
  distDir: process.env.NEXT_DIST_DIR || ".next",
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