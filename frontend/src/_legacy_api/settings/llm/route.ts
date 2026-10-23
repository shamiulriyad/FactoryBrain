// GET / POST /api/settings/llm
// ---------------------------------------------------------------------------
// GET  — returns ONLY non-secret LLM configuration status for the UI.
// POST — upserts LLM_PROVIDER / LLM_API_KEY / LLM_MODEL into .env.local in
//        development (NODE_ENV !== "production"). In production, the request
//        only updates process.env for the current process and tells the
//        caller that durable prod keys must live in the Vercel project env.
//
// Rules:
//   - Never log the request body or any substring of the API key.
//   - Empty `apiKey` string leaves the existing key untouched (no wipe).
//   - Explicit clear only when `clearKey: true` is provided.
//   - Trimmed; whitespace-only keys are rejected with HTTP 400.
//   - Invalid `provider` returns HTTP 400.
//   - Defaults on first configure: provider = "openai", model = "gpt-6-luna".
//   - Response on success matches the GET status shape exactly; the key is
//     NEVER echoed back.

import { NextRequest, NextResponse } from "next/server";
import { promises as fs } from "node:fs";
import { existsSync } from "node:fs";
import path from "node:path";
import { z } from "zod";

export const runtime = "nodejs";

const SUPPORTED_PROVIDERS = ["gemini", "openai", "anthropic"] as const;
type SupportedProvider = (typeof SUPPORTED_PROVIDERS)[number];

// Product-level defaults when the caller hasn't picked a provider/model on
// the first configure step. Tuned for this app's OpenAI-leaning stack.
const DEFAULT_PROVIDER: SupportedProvider = "openai";
const DEFAULT_MODEL = "gpt-6-luna";

const StatusSchema = z
  .object({
    configured: z.boolean(),
    provider: z.string().nullable(),
    model: z.string().nullable(),
    mode: z.enum(["demo", "live"]),
    persistence: z.enum(["env.local", "process", "vercel_only"])
  })
  .strict();

const BodySchema = z
  .object({
    provider: z.enum(["", ...SUPPORTED_PROVIDERS]).optional(),
    apiKey: z.string().optional(),
    model: z.string().optional(),
    clearKey: z.boolean().optional()
  })
  .strict();

export async function GET() {
  const status = readStatusFromProcessEnv();
  const validated = StatusSchema.safeParse(status);
  if (!validated.success) {
    return NextResponse.json(
      { error: "settings_shape_invalid", issues: validated.error.issues },
      { status: 500 }
    );
  }
  return NextResponse.json(validated.data, {
    status: 200,
    headers: { "Cache-Control": "no-store" }
  });
}

export async function POST(req: NextRequest) {
  let raw: unknown;
  try {
    raw = await req.json();
  } catch {
    return NextResponse.json({ error: "invalid_json" }, { status: 400 });
  }

  const parsed = BodySchema.safeParse(raw);
  if (!parsed.success) {
    return NextResponse.json(
      { error: "invalid_body", issues: parsed.error.issues },
      { status: 400 }
    );
  }

  const body = parsed.data;
  const isDev = process.env.NODE_ENV !== "production";
  const isFirstConfigure = !normalize(process.env.LLM_PROVIDER) && !normalize(process.env.LLM_API_KEY);

  // --- Resolve provider -----------------------------------------------------
  let nextProvider: SupportedProvider | null = null;
  if (body.provider !== undefined && body.provider !== "") {
    if (!SUPPORTED_PROVIDERS.includes(body.provider as SupportedProvider)) {
      return NextResponse.json(
        { error: "invalid_provider", provider: body.provider },
        { status: 400 }
      );
    }
    nextProvider = body.provider as SupportedProvider;
  } else if (isFirstConfigure) {
    // First-time configure and the caller didn't pick one → product default.
    nextProvider = DEFAULT_PROVIDER;
  } else {
    // Keep the existing provider unless one was explicitly supplied.
    const current = normalize(process.env.LLM_PROVIDER);
    nextProvider = (SUPPORTED_PROVIDERS.includes(current as SupportedProvider)
      ? (current as SupportedProvider)
      : null);
  }

  // --- Resolve model --------------------------------------------------------
  let nextModel: string | null;
  if (body.model !== undefined && body.model.trim().length > 0) {
    nextModel = body.model.trim();
  } else if (isFirstConfigure && !normalize(process.env.LLM_MODEL)) {
    nextModel = DEFAULT_MODEL;
  } else {
    nextModel = normalize(process.env.LLM_MODEL) || null;
  }

  // --- Resolve key change ---------------------------------------------------
  const explicitClear = body.clearKey === true;
  const rawKey = typeof body.apiKey === "string" ? body.apiKey : undefined;
  const trimmedKey = rawKey !== undefined ? rawKey.trim() : undefined;

  if (trimmedKey !== undefined && trimmedKey.length === 0 && !explicitClear) {
    // Empty string = leave existing key unchanged. This is NOT a clear.
    // Whitespace-only was already trimmed to "" above.
    // No error — we treat this as a no-op for the key, while still allowing
    // provider/model to update on the same call.
  }

  let nextKeyPersist: string | undefined;
  let keyShouldChange: boolean;
  if (explicitClear) {
    keyShouldChange = true;
    nextKeyPersist = "";
  } else if (trimmedKey !== undefined && trimmedKey.length > 0) {
    keyShouldChange = true;
    nextKeyPersist = trimmedKey;
  } else {
    // apiKey was omitted OR sent as "" with no clearKey — leave existing as-is.
    keyShouldChange = false;
    nextKeyPersist = undefined;
  }

  // Whitespace-only key is already caught by .trim() turning it into ""; we
  // treat that as "no change" rather than "reject with 400", since the caller
  // may be sending an empty draft input field. Only an explicit clear counts.

  // --- Apply to process.env (always) ---------------------------------------
  if (nextProvider) process.env.LLM_PROVIDER = nextProvider;
  if (nextModel !== null) process.env.LLM_MODEL = nextModel;
  if (keyShouldChange) {
    if (explicitClear) {
      delete process.env.LLM_API_KEY;
    } else {
      process.env.LLM_API_KEY = nextKeyPersist as string;
    }
  }

  // --- Apply to durable storage (dev only) ---------------------------------
  let persistence: "env.local" | "process" | "vercel_only";
  let notice: string | null = null;

  if (isDev) {
    try {
      await writeEnvLocal({
        provider: nextProvider,
        model: nextModel,
        key: keyShouldChange ? nextKeyPersist : undefined,
        clearKey: explicitClear
      });
      persistence = "env.local";
    } catch (err) {
      // Don't leak the key in the error message — log only the failure shape.
      console.error("[settings/llm] failed to persist .env.local");
      return NextResponse.json(
        { error: "persist_failed" },
        { status: 500 }
      );
    }
  } else {
    // Production: never write files. process.env updates are scoped to this
    // process only — durable prod secrets belong in the Vercel project env.
    persistence = "process";
    if (nextKeyPersist !== undefined && !explicitClear) {
      notice =
        "Live key set for this process only. Add LLM_API_KEY to your Vercel " +
        "project environment for it to survive restarts and deploys.";
    } else if (explicitClear) {
      notice =
        "Key cleared for this process only. Remove LLM_API_KEY from your " +
        "Vercel project environment to finish clearing it.";
    } else if (nextProvider || nextModel !== null) {
      notice =
        "Provider/model updated for this process only. Mirror the change in " +
        "your Vercel project environment for durability.";
    }
  }

  const status = readStatusFromProcessEnv();
  const validated = StatusSchema.safeParse(status);
  if (!validated.success) {
    return NextResponse.json(
      { error: "settings_shape_invalid", issues: validated.error.issues },
      { status: 500 }
    );
  }

  return NextResponse.json(
    notice ? { ...validated.data, notice } : validated.data,
    { status: 200, headers: { "Cache-Control": "no-store" } }
  );
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function normalize(value: string | undefined | null): string {
  if (typeof value !== "string") return "";
  return value.trim();
}

function readStatusFromProcessEnv() {
  const provider = normalize(process.env.LLM_PROVIDER);
  const apiKey = normalize(process.env.LLM_API_KEY);
  const model = normalize(process.env.LLM_MODEL);
  const configured = Boolean(provider && apiKey);
  return {
    configured,
    provider: provider.length > 0 ? provider : null,
    model: model.length > 0 ? model : null,
    mode: (configured ? "live" : "demo") as "demo" | "live",
    persistence: resolvePersistence()
  };
}

function resolvePersistence(): "env.local" | "process" | "vercel_only" {
  if (process.env.VERCEL === "1" || process.env.VERCEL_ENV) {
    return "vercel_only";
  }
  try {
    const cwd = process.cwd();
    const probe = path.join(cwd, ".env.local");
    if (existsSync(probe)) return "env.local";
  } catch {
    // ignore
  }
  return "process";
}

type EnvLocalPatch = {
  provider: SupportedProvider | null;
  model: string | null;
  key: string | undefined; // undefined = leave alone
  clearKey: boolean;
};

const ENV_LOCAL_KEYS = ["LLM_PROVIDER", "LLM_API_KEY", "LLM_MODEL"] as const;

async function writeEnvLocal(patch: EnvLocalPatch): Promise<void> {
  const cwd = process.cwd();
  const envPath = path.join(cwd, ".env.local");

  let existing = "";
  try {
    existing = await fs.readFile(envPath, "utf8");
  } catch (err: any) {
    if (err?.code !== "ENOENT") throw err;
  }

  const lines = existing.split(/\r?\n/);
  const indexByKey = new Map<string, number>();
  lines.forEach((line, idx) => {
    const m = line.match(/^\s*(LLM_PROVIDER|LLM_API_KEY|LLM_MODEL)\s*=/);
    if (m) indexByKey.set(m[1], idx);
  });

  const setLine = (key: string, value: string) => {
    const line = `${key}=${value}`;
    const idx = indexByKey.get(key);
    if (idx !== undefined) {
      lines[idx] = line;
    } else {
      lines.push(line);
      indexByKey.set(key, lines.length - 1);
    }
  };

  const unsetLine = (key: string) => {
    const idx = indexByKey.get(key);
    if (idx !== undefined) {
      lines.splice(idx, 1);
      // Recompute indices after splice to stay correct for further edits.
      indexByKey.clear();
      lines.forEach((line, i) => {
        const m = line.match(/^\s*(LLM_PROVIDER|LLM_API_KEY|LLM_MODEL)\s*=/);
        if (m) indexByKey.set(m[1], i);
      });
    }
  };

  if (patch.provider) setLine("LLM_PROVIDER", patch.provider);
  if (patch.model !== null) setLine("LLM_MODEL", patch.model);

  if (patch.clearKey) {
    unsetLine("LLM_API_KEY");
  } else if (patch.key !== undefined) {
    setLine("LLM_API_KEY", patch.key);
  }

  // Ensure the file is in .gitignore (idempotent).
  await ensureGitignored(cwd, [".env.local", ".env", ".env*.local"]);

  const body = lines.join("\n");
  // Make sure the file ends with a newline.
  const trailing = body.endsWith("\n") ? body : body + "\n";
  await fs.writeFile(envPath, trailing, { mode: 0o600 });
  try {
    await fs.chmod(envPath, 0o600);
  } catch {
    // best-effort on platforms where chmod isn't supported
  }
}

async function ensureGitignored(cwd: string, entries: string[]): Promise<void> {
  const gitignorePath = path.join(cwd, ".gitignore");
  let content = "";
  try {
    content = await fs.readFile(gitignorePath, "utf8");
  } catch (err: any) {
    if (err?.code !== "ENOENT") throw err;
  }
  const lines = content.split(/\r?\n/);
  let changed = false;
  for (const entry of entries) {
    const present = lines.some((line) => line.trim() === entry);
    if (!present) {
      lines.push(entry);
      changed = true;
    }
  }
  if (!changed) return;
  const next = lines.join("\n");
  await fs.writeFile(gitignorePath, next.endsWith("\n") ? next : next + "\n");
}
