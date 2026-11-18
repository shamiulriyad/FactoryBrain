"use client";

import { useEffect, useState } from "react";
import { ExternalLink, Save, Trash2, Youtube } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Field } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { SkeletonRows } from "@/components/ui/Status";
import { useResource } from "@/lib/useResource";
import { apiJson, errorMessage } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import { P } from "@/lib/access";

/** Public-website settings. Today: the homepage demo video. */
function WebsiteSettingsInner() {
  return (
    <div className="mx-auto w-full max-w-[1200px] px-4 py-8 md:px-8">
      <PageHeader
        eyebrow="Administration"
        title="Website"
        description="Settings for the public website. Changes are recorded in the audit log."
        actions={
          <a href="/" target="_blank" rel="noopener" className="inline-flex items-center gap-1.5 text-body text-accent hover:underline">
            Open homepage <ExternalLink size={14} aria-hidden />
          </a>
        }
      />
      <DemoVideoPanel />
    </div>
  );
}

interface StoredVideo {
  configured: boolean;
  value?: { videoId: string; title?: string | null };
  updatedAt?: string;
}

/** Same rules as the API: watch, youtu.be, embed, shorts or live links, or a bare 11-character id. */
function youTubeId(input: string): string | null {
  const url = input.trim();
  const m = url.match(/^(?:https?:\/\/)?(?:(?:www\.|m\.|music\.)?youtube(?:-nocookie)?\.com\/(?:watch\?(?:.*&)?v=|embed\/|shorts\/|live\/|v\/)|youtu\.be\/)([A-Za-z0-9_-]{11})(?:[?&#/].*)?$/i);
  return m ? m[1] : /^[A-Za-z0-9_-]{11}$/.test(url) ? url : null;
}

/** The YouTube demo video on the homepage. */
function DemoVideoPanel() {
  const res = useResource<StoredVideo>("/api/site-settings/demo-video");
  const [url, setUrl] = useState("");
  const [title, setTitle] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ tone: "success" | "danger"; text: string } | null>(null);

  useEffect(() => {
    const v = res.data?.configured ? res.data.value : null;
    setUrl(v ? `https://www.youtube.com/watch?v=${v.videoId}` : "");
    setTitle(v?.title ?? "");
  }, [res.data]);

  const id = youTubeId(url);
  const saved = res.data?.configured ? res.data.value : null;
  const dirty = (id ?? "") !== (saved?.videoId ?? "") || title.trim() !== (saved?.title ?? "");

  const save = async () => {
    setBusy(true);
    setMessage(null);
    try {
      await apiJson("/api/site-settings/demo-video", { method: "PUT", body: { url, title: title.trim() || null } });
      await res.reload();
      setMessage({ tone: "success", text: "Saved. The homepage shows the video within about a minute." });
    } catch (e) {
      setMessage({ tone: "danger", text: errorMessage(e) });
    } finally {
      setBusy(false);
    }
  };

  const remove = async () => {
    setBusy(true);
    setMessage(null);
    try {
      await apiJson("/api/site-settings/demo-video", { method: "DELETE" });
      await res.reload();
      setMessage({ tone: "success", text: "Removed. The homepage shows the placeholder again." });
    } catch (e) {
      setMessage({ tone: "danger", text: errorMessage(e) });
    } finally {
      setBusy(false);
    }
  };

  return (
    <Panel className="mt-6 p-5" testId="demo-video-panel">
      <h2 className="flex items-center gap-2 text-body font-semibold text-fg-primary">
        <Youtube size={16} aria-hidden /> Demo video
      </h2>
      <p className="mt-1 text-caption text-fg-secondary">
        Paste a YouTube link (watch, youtu.be, shorts or embed). It plays in the &quot;Product demo&quot; section of the homepage; until a video is set,
        that section offers a live demo instead. Unlisted videos work too.
      </p>
      {!res.data && !res.error ? (
        <SkeletonRows rows={2} />
      ) : (
        <div className="mt-5 grid gap-6 lg:grid-cols-[minmax(0,1fr)_minmax(0,0.9fr)]">
          <div className="space-y-4">
            <Field label="YouTube link" error={url.trim() && !id ? "That isn't a YouTube video link." : undefined}>
              {(f) => (
                <Input id={f.id} aria-describedby={f.describedBy} aria-invalid={f.invalid} value={url} placeholder="https://www.youtube.com/watch?v=…" onChange={(e) => { setUrl(e.target.value); setMessage(null); }} />
              )}
            </Field>
            <Field label="Title (optional)">
              {(f) => (
                <Input id={f.id} value={title} maxLength={120} placeholder="BunonBrain product demo" onChange={(e) => { setTitle(e.target.value); setMessage(null); }} />
              )}
            </Field>
            {message && <Callout tone={message.tone}>{message.text}</Callout>}
            <div className="flex flex-wrap items-center justify-between gap-3">
              <p className="text-caption text-fg-tertiary">
                {res.data?.configured && res.data.updatedAt ? `Last saved ${formatDateTime(res.data.updatedAt)}` : "No video set yet."}
              </p>
              <div className="flex gap-2">
                {saved && (
                  <Button variant="secondary" onClick={remove} disabled={busy}>
                    <Trash2 size={15} aria-hidden /> Remove
                  </Button>
                )}
                <Button onClick={save} disabled={busy || !id || !dirty}>
                  <Save size={15} aria-hidden /> {busy ? "Saving…" : "Save"}
                </Button>
              </div>
            </div>
          </div>
          <div>
            <p className="mb-2 text-caption text-fg-tertiary">Preview</p>
            {id ? (
              <div className="relative aspect-video overflow-hidden rounded-lg border border-border-subtle bg-black">
                <iframe
                  className="absolute inset-0 size-full"
                  src={`https://www.youtube-nocookie.com/embed/${id}?rel=0`}
                  title={title || "Demo video preview"}
                  allow="accelerometer; clipboard-write; encrypted-media; gyroscope; picture-in-picture"
                  allowFullScreen
                />
              </div>
            ) : (
              <div className="flex aspect-video items-center justify-center rounded-lg border border-dashed border-border-subtle text-caption text-fg-tertiary">
                Paste a link to preview it here.
              </div>
            )}
          </div>
        </div>
      )}
    </Panel>
  );
}

export default function WebsiteSettingsPage() {
  return (
    <RequirePermission perms={[P.PlatformSettingsManage]}>
      <WebsiteSettingsInner />
    </RequirePermission>
  );
}
