"use client";

// Predicted physical activity of temporary camera tracks: badges, the current
// summary, distributions by camera / line and the track table. Every view keeps
// four things apart: a detected person, its temporary track id, the predicted
// activity, and a verified worker identity (only ever from an RFID card, never
// from the camera). Nothing here is a productivity or performance measure.

import Link from "next/link";
import { Box, HelpCircle, MapPin, PersonStanding, ScanLine, Scissors, ShieldCheck, UserRound } from "lucide-react";
import { Badge } from "@/components/ui/Badge";
import { cn } from "@/lib/cn";
import {
  MON, UNCERTAIN_REASON_LABEL, activityColor, activityName, ago, dhaka, dhakaTime, duration,
  type ActivityDistribution, type ActivityModel, type ActivityTrack, type MonitoringActivity
} from "@/lib/monitoring";

const label = activityName;

/** "Walking" with its colour; UNKNOWN reads "Unknown" with the reason. Always titled as a prediction. */
export function ActivityBadge({ activity, previous, reason, confidence, className, garment }: {
  activity: string; previous?: string | null; reason?: string | null; confidence?: number | null; className?: string; garment?: boolean;
}) {
  const unknown = activity === "UNKNOWN";
  const what = garment ? "garment activity (trained model)" : "posture / movement";
  const title = unknown
    ? `The ${what} could not be predicted${reason ? `: ${UNCERTAIN_REASON_LABEL[reason] ?? reason.toLowerCase()}` : ""}`
    : `Predicted ${what}${previous ? ` (was ${label(previous)})` : ""}. A prediction about a temporary track, not verified work.`;
  return (
    <span className={cn("inline-flex items-center gap-1 h-5 px-1.5 rounded-sm border text-[11px] font-medium shrink-0",
      unknown ? "border-dashed border-border-strong text-fg-secondary" : "border-border-subtle text-fg-primary", className)}
      title={title} data-testid="activity-badge" data-activity={activity} data-kind={garment ? "garment" : "generic"}>
      {garment && <Scissors size={10} className="text-fg-tertiary" aria-label="Garment activity" />}
      {unknown ? <HelpCircle size={11} aria-hidden /> : <span className="h-2 w-2 rounded-full" style={{ background: activityColor(activity) }} aria-hidden />}
      {previous && !unknown && <span className="text-fg-tertiary">{label(previous)} →</span>}
      {label(activity)}
      {confidence != null && <span className="text-fg-tertiary tabular-nums">{confidence.toFixed(2)}</span>}
      {unknown && reason && <span className="text-fg-tertiary">· {UNCERTAIN_REASON_LABEL[reason] ?? reason.toLowerCase()}</span>}
    </span>
  );
}

/** What a monitoring view must never blur together. */
export function IdentityLegend({ className }: { className?: string }) {
  const items = [
    { icon: <ScanLine size={13} aria-hidden />, title: "Detected person", body: "A person-shaped detection in one camera's picture." },
    { icon: <span className="font-mono text-[11px]">t17</span>, title: "Temporary track id", body: "A number the camera gives a moving person. It changes on restart and differs per camera." },
    { icon: <PersonStanding size={13} aria-hidden />, title: "Generic posture", body: "Standing, sitting, walking, bending, reaching: rules over the pose. Can be wrong; UNKNOWN when unsure." },
    { icon: <Scissors size={13} aria-hidden />, title: "Garment activity", body: "Sewing, cutting, packing…: a trained model's prediction, with its version and evaluation status. UNKNOWN when unsure." },
    { icon: <MapPin size={13} aria-hidden />, title: "Workstation", body: "Where the person is (camera zone). Context only: being at a machine doesn't mean using it." },
    { icon: <ShieldCheck size={13} aria-hidden />, title: "Verified worker", body: "Only from an active RFID card on the event. The camera never identifies anyone." }
  ];
  return (
    <ul className={cn("grid gap-2 sm:grid-cols-2 xl:grid-cols-3", className)} aria-label="What the labels mean" data-testid="identity-legend">
      {items.map((i) => (
        <li key={i.title} className="rounded-[var(--radius-control)] border border-border-subtle px-3 py-2">
          <p className="flex items-center gap-1.5 text-caption font-medium text-fg-primary">{i.icon}{i.title}</p>
          <p className="text-caption text-fg-tertiary mt-0.5">{i.body}</p>
        </li>
      ))}
    </ul>
  );
}

/** Current tracks per predicted activity (tiles), plus how often predictions were uncertain. */
export function ActivitySummary({ data, compact }: { data: MonitoringActivity; compact?: boolean }) {
  const c = data.counts;
  return (
    <div className={cn("grid gap-2", compact ? "grid-cols-3 sm:grid-cols-6" : "grid-cols-2 sm:grid-cols-4 xl:grid-cols-7")} data-testid="activity-summary" data-kind={data.kind}>
      {!compact && (
        <div className="rounded-[var(--radius-control)] border border-border-subtle px-3 py-2">
          <p className="text-caption text-fg-secondary">Tracks now</p>
          <p className="text-xl font-semibold tabular-nums">{c.currentTracks}</p>
          <p className="text-[11px] text-fg-tertiary">active in the last {Math.round(data.activeSeconds / 60)} min</p>
        </div>
      )}
      {data.activities.map((a) => {
        const n = c.current.find((x) => x.activity === a)?.tracks ?? 0;
        return (
          <div key={a} className={cn("rounded-[var(--radius-control)] border px-3 py-2", a === "UNKNOWN" ? "border-dashed border-border-strong" : "border-border-subtle")}
            data-testid={`activity-tile-${a}`}>
            <p className="flex items-center gap-1.5 text-caption text-fg-secondary">
              {a === "UNKNOWN" ? <HelpCircle size={11} aria-hidden /> : <span className="h-2 w-2 rounded-full" style={{ background: activityColor(a) }} aria-hidden />}
              {label(a)}
            </p>
            <p className="text-xl font-semibold tabular-nums">{n}</p>
            {!compact && <p className="text-[11px] text-fg-tertiary">{n === 1 ? "track" : "tracks"} now</p>}
          </div>
        );
      })}
      {!compact && (
        <p className="col-span-full text-caption text-fg-tertiary">
          In range: {c.stateChanges} change(s), {c.classified} confirmation(s), {c.uncertain} uncertain period(s)
          {c.uncertainRate != null && <> · <span title="Share of activity events that were ACTIVITY_UNCERTAIN">{Math.round(c.uncertainRate * 100)}% of activity events uncertain</span></>}
        </p>
      )}
    </div>
  );
}

/** One stacked bar per camera or line: observed seconds per predicted activity. */
export function ActivityDistribution({ rows, empty, testId, linkFor, order }: {
  rows: ActivityDistribution[]; empty: string; testId?: string; linkFor?: (r: ActivityDistribution) => string; order: string[];
}) {
  if (rows.length === 0) return <p className="px-4 py-6 text-caption text-fg-tertiary">{empty}</p>;
  return (
    <ul className="divide-y divide-border-subtle" data-testid={testId}>
      {rows.map((r) => {
        const total = r.observedSeconds || 1;
        const rank = (a: string) => (order.indexOf(a) < 0 ? order.length : order.indexOf(a));
        const shares = [...r.activities].sort((a, b) => rank(a.activity) - rank(b.activity));
        const text = shares.map((s) => `${label(s.activity)} ${Math.round((s.seconds / total) * 100)}%`).join(", ");
        const name = linkFor ? <Link className="hover:underline" href={linkFor(r)}>{r.name}</Link> : r.name;
        return (
          <li key={r.key} className="px-4 py-3" data-testid="activity-distribution-row">
            <div className="flex flex-wrap items-baseline justify-between gap-2">
              <span className="font-medium text-fg-primary">{name}</span>
              <span className="text-caption text-fg-tertiary">
                {duration(r.observedSeconds)} observed · {r.tracks} track(s) ·{" "}
                <span title="Share of activity predictions whose activity was UNKNOWN (including labels outside the current catalog)">
                  {r.predictions ? Math.round((r.uncertainPredictions / r.predictions) * 100) : 0}% unknown
                </span>
              </span>
            </div>
            <div className="mt-2 flex h-3 w-full overflow-hidden rounded-full bg-surface-2" role="img" aria-label={`${r.name}: ${text || "no observed time"}`}>
              {shares.map((s) => s.seconds > 0 && (
                <span key={s.activity} className={cn("h-full", s.activity === "UNKNOWN" && "opacity-70")}
                  style={{ width: `${(s.seconds / total) * 100}%`, background: activityColor(s.activity) }}
                  title={`${label(s.activity)}: ${duration(s.seconds)} (${s.predictions} prediction(s))`} />
              ))}
            </div>
            <p className="mt-1 text-[11px] text-fg-tertiary">{text || "Predictions without observed time yet."}</p>
          </li>
        );
      })}
    </ul>
  );
}

export function ActivityLegend({ activities }: { activities: string[] }) {
  return (
    <ul className="flex flex-wrap gap-3 text-caption text-fg-secondary" aria-label="Activity colours">
      {activities.map((a) => (
        <li key={a} className="inline-flex items-center gap-1.5"><span className="h-2.5 w-2.5 rounded-full" style={{ background: activityColor(a) }} aria-hidden />{label(a)}</li>
      ))}
    </ul>
  );
}

/** The models behind the predictions, and whether each was evaluated on a held-out test set. */
export function ActivityModels({ models, kind }: { models: ActivityModel[]; kind: string }) {
  if (models.length === 0)
    return <p className="text-caption text-fg-tertiary">{kind === "garment" ? "No garment model has reported in this range. Garment-specific accuracy stays PENDING until a model is trained and evaluated on labelled factory footage." : "No predictions in this range."}</p>;
  return (
    <ul className="flex flex-wrap gap-2" data-testid="activity-models">
      {models.map((m) => (
        <li key={`${m.name}/${m.version}`} className="inline-flex items-center gap-2 rounded-[var(--radius-control)] border border-border-subtle px-2.5 py-1.5 text-caption">
          <Box size={12} className="text-fg-tertiary" aria-hidden />
          <span className="font-mono">{m.name}</span>{m.version && <span className="text-fg-tertiary">v{m.version}</span>}
          {kind === "garment" && (
            <Badge tone={m.evaluationStatus === "EVALUATED" ? "success" : "warning"}
              title={m.evaluationStatus === "EVALUATED" ? "Scored on a held-out test split before use" : "Not evaluated on a held-out test split: development use only"}>
              {m.evaluationStatus === "EVALUATED" ? "Evaluated" : "Not evaluated"}
            </Badge>
          )}
          <span className="text-fg-tertiary">{m.events} event(s)</span>
        </li>
      ))}
    </ul>
  );
}

const th = "px-3 py-2 text-left text-caption font-medium text-fg-tertiary whitespace-nowrap";
const td = "px-3 py-2 align-top text-caption";

/** Track-level details: camera, temporary track id, predicted activity, confidence, since when, identity. */
export function ActivityTrackTable({ tracks, showCamera = true, empty, garment }: { tracks: ActivityTrack[]; showCamera?: boolean; empty: string; garment?: boolean }) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[44rem]" data-testid="activity-tracks">
        <thead>
          <tr>
            {showCamera && <th className={th}>Camera</th>}
            <th className={th}><span title="Temporary camera-local number, not a worker">Temporary track</span></th>
            <th className={th}>{garment ? "Predicted garment activity" : "Predicted posture"}</th>
            <th className={th}>Confidence</th>
            <th className={th}>Since (Dhaka)</th>
            <th className={th}>Last prediction</th>
            {garment && <th className={th}>Model</th>}
            <th className={th}>Identity</th>
          </tr>
        </thead>
        <tbody>
          {tracks.length === 0 ? (
            <tr><td className={cn(td, "py-8 text-center text-fg-tertiary")} colSpan={(showCamera ? 7 : 6) + (garment ? 1 : 0)}>{empty}</td></tr>
          ) : tracks.map((t) => (
            <tr key={`${t.cameraId}/${t.trackId}`} className={cn(!t.current && "opacity-60")} data-testid="activity-track-row" data-activity={t.activity}>
              {showCamera && (
                <td className={td}>
                  <Link className="font-mono hover:underline" href={`${MON}/cameras/${t.cameraId}`}>{t.cameraCode}</Link>
                  {t.line && <p className="text-[11px] text-fg-tertiary">{t.line.name}</p>}
                </td>
              )}
              <td className={td}>
                <span className="font-mono">{t.trackId}</span>
                <p className="text-[11px] text-fg-tertiary">{t.current ? "in view" : t.ended ? "left the view" : "not seen recently"}</p>
              </td>
              <td className={td}>
                <ActivityBadge activity={t.activity} reason={t.reason} garment={garment} />
                {t.uncertain && t.candidate && <p className="text-[11px] text-fg-tertiary mt-0.5">best guess {label(t.candidate)}, not asserted</p>}
                {t.sourceType === "SIMULATED" && <p className="text-[11px] text-[var(--risk-medium)] mt-0.5">SIMULATED</p>}
              </td>
              <td className={cn(td, "tabular-nums")}>{t.confidence != null ? t.confidence.toFixed(2) : "—"}</td>
              <td className={cn(td, "tabular-nums whitespace-nowrap")} title={dhaka(t.since)}>{dhakaTime(t.since)}</td>
              <td className={cn(td, "whitespace-nowrap")} title={dhaka(t.lastPredictionAt)}>{ago(t.lastPredictionAt)}</td>
              {garment && <td className={cn(td, "font-mono whitespace-nowrap")}>{t.modelName ?? "—"}{t.modelVersion ? ` v${t.modelVersion}` : ""}</td>}
              <td className={td}>
                {t.verifiedWorker
                  ? <Badge tone="accent" title="Identity verified by an RFID card on the event">{t.verifiedWorker.code}</Badge>
                  : <span className="inline-flex items-center gap-1 text-fg-tertiary" title="The camera never identifies anyone"><UserRound size={12} aria-hidden />Not identified</span>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
