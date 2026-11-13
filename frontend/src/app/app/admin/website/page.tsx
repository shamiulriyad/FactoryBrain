"use client";

import Image from "next/image";
import { useEffect, useState } from "react";
import { ArrowUp, Check, ExternalLink, Save } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect } from "@/components/ui/Field";
import { SkeletonRows } from "@/components/ui/Status";
import { HERO_DEFAULTS, HERO_IMAGES, resolveHero, type HeroSettings } from "@/content/site/heroMedia";
import { useResource } from "@/lib/useResource";
import { apiJson, errorMessage } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import { P } from "@/lib/access";
import { cn } from "@/lib/cn";

interface Stored {
  configured: boolean;
  value?: HeroSettings;
  updatedAt?: string;
}

const INTERVALS = [2, 3, 4, 5, 6, 9, 12, 15, 20, 30];

/** Choose the homepage hero photos (from the assets in heroMedia.ts) and whether they rotate. */
function WebsiteSettingsInner() {
  const res = useResource<Stored>("/api/site-settings/hero");
  const [ids, setIds] = useState<string[]>(HERO_DEFAULTS.imageIds);
  const [rotate, setRotate] = useState(false);
  const [interval, setIntervalSec] = useState(HERO_DEFAULTS.intervalSeconds);
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState<{ tone: "success" | "danger"; text: string } | null>(null);

  // Load what is saved (unknown ids are dropped, as on the website).
  useEffect(() => {
    if (!res.data) return;
    const r = resolveHero(res.data.configured ? res.data.value : null);
    setIds(r.images.map((i) => i.id));
    setRotate(Boolean(res.data.value?.rotate) && r.images.length > 1);
    setIntervalSec(r.intervalSeconds);
  }, [res.data]);

  const toggle = (id: string) => {
    setMessage(null);
    setIds((cur) => (cur.includes(id) ? (cur.length > 1 ? cur.filter((x) => x !== id) : cur) : [...cur, id]));
  };
  const makeFirst = (id: string) => setIds((cur) => [id, ...cur.filter((x) => x !== id)]);

  const save = async () => {
    setSaving(true);
    setMessage(null);
    try {
      await apiJson("/api/site-settings/hero", { method: "PUT", body: { imageIds: ids, rotate: rotate && ids.length > 1, intervalSeconds: interval } });
      await res.reload();
      setMessage({ tone: "success", text: "Saved. The homepage shows the change within about a minute." });
    } catch (e) {
      setMessage({ tone: "danger", text: errorMessage(e) });
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="mx-auto w-full max-w-[1200px] px-4 py-8 md:px-8">
      <PageHeader
        eyebrow="Administration"
        title="Website"
        description="Choose the photographs shown in the homepage hero and whether they rotate. Changes are recorded in the audit log."
        actions={
          <a href="/" target="_blank" rel="noopener" className="inline-flex items-center gap-1.5 text-body text-accent hover:underline">
            Open homepage <ExternalLink size={14} aria-hidden />
          </a>
        }
      />

      {res.error && !res.data && (
        <Callout tone="danger" className="mt-6">
          {errorMessage(res.error)}
        </Callout>
      )}
      {!res.data && !res.error ? (
        <Panel className="mt-6">
          <SkeletonRows rows={4} />
        </Panel>
      ) : (
        <>
          <Panel className="mt-6 p-5">
            <h2 className="text-body font-semibold text-fg-primary">Hero photographs</h2>
            <p className="mt-1 text-caption text-fg-secondary">
              Select one or more. The first selected photo is shown first (and is the only one shown when rotation is off). New photos are added to
              the website&apos;s photo library by a developer (frontend/src/content/site/heroMedia.ts).
            </p>
            <ul className="mt-5 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
              {HERO_IMAGES.map((img) => {
                const pos = ids.indexOf(img.id);
                const selected = pos >= 0;
                return (
                  <li key={img.id}>
                    <div className={cn("overflow-hidden rounded-xl border-2 transition-colors", selected ? "border-[var(--accent)]" : "border-border-subtle")}>
                      <button type="button" onClick={() => toggle(img.id)} aria-pressed={selected} className="relative block aspect-[3/2] w-full">
                        <Image src={img.src} alt={img.alt} fill sizes="280px" className="object-cover" style={{ objectPosition: img.focus }} />
                        {selected && (
                          <span className="absolute left-2 top-2 inline-flex size-7 items-center justify-center rounded-full bg-[var(--accent)] text-caption font-semibold text-white">
                            {pos + 1}
                          </span>
                        )}
                        <span className="sr-only">{selected ? "Selected. Press to remove." : "Press to select."}</span>
                      </button>
                      <div className="p-3">
                        <p className="text-caption font-medium text-fg-primary">{img.caption}</p>
                        <p className="text-caption text-fg-tertiary">
                          {img.credit.name} / {img.credit.source}
                        </p>
                        <div className="mt-2 flex flex-wrap gap-2">
                          <Button size="sm" variant={selected ? "secondary" : "primary"} onClick={() => toggle(img.id)} disabled={selected && ids.length === 1}>
                            {selected ? "Remove" : (
                              <>
                                <Check size={14} aria-hidden /> Select
                              </>
                            )}
                          </Button>
                          {selected && pos > 0 && (
                            <Button size="sm" variant="secondary" onClick={() => makeFirst(img.id)}>
                              <ArrowUp size={14} aria-hidden /> Show first
                            </Button>
                          )}
                          {pos === 0 && <Badge tone="success">Shown first</Badge>}
                        </div>
                      </div>
                    </div>
                  </li>
                );
              })}
            </ul>
          </Panel>

          <Panel className="mt-4 p-5">
            <h2 className="text-body font-semibold text-fg-primary">Rotation</h2>
            <label className="mt-3 flex items-start gap-3">
              <input
                type="checkbox"
                className="mt-1 size-4 accent-[var(--accent)]"
                checked={rotate && ids.length > 1}
                disabled={ids.length < 2}
                onChange={(e) => setRotate(e.target.checked)}
              />
              <span>
                <span className="block text-body text-fg-primary">Rotate between the selected photos</span>
                <span className="block text-caption text-fg-secondary">
                  A slow crossfade that pauses while a visitor hovers over the photo. Visitors who ask for reduced motion always see the first photo.
                  {ids.length < 2 && " Select at least two photos to enable rotation."}
                </span>
              </span>
            </label>
            <div className="mt-4 max-w-xs">
              <FilterSelect label="Each photo stays for" value={String(interval)} onChange={(v) => setIntervalSec(Number(v))}>
                {INTERVALS.map((s) => (
                  <option key={s} value={s}>
                    {s} seconds
                  </option>
                ))}
              </FilterSelect>
            </div>
          </Panel>

          {message && (
            <Callout tone={message.tone} className="mt-4">
              {message.text}
            </Callout>
          )}
          <div className="mt-5 flex flex-wrap items-center justify-between gap-3">
            <p className="text-caption text-fg-tertiary">
              {res.data?.configured && res.data.updatedAt ? `Last saved ${formatDateTime(res.data.updatedAt)}` : "Using the default photo (nothing saved yet)."}
            </p>
            <Button onClick={save} disabled={saving}>
              <Save size={15} aria-hidden /> {saving ? "Saving…" : "Save"}
            </Button>
          </div>
        </>
      )}
    </div>
  );
}

export default function WebsiteSettingsPage() {
  return (
    <RequirePermission perms={[P.PlatformSettingsManage]}>
      <WebsiteSettingsInner />
    </RequirePermission>
  );
}
