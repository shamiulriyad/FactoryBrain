"use client";

import { useMemo } from "react";
import { motion, AnimatePresence } from "framer-motion";
import { useT } from "@/lib/useT";
import { useBusinessStore } from "@/store/business.store";
import { useFactoryStore } from "@/store/factory.store";

type Bubble = {
  id: string;
  direction: "in" | "out";
  text: string;
  timestamp: string;
};

/** Pure UI; derives chat bubbles from the factory store's recent events. */
export function WhatsAppAlert() {
  const { t, locale } = useT();
  const enabled = useBusinessStore((s) => s.featureFlags.whatsappAlert);
  const events = useFactoryStore((s) => s.recentEvents);

  const bubbles = useMemo<Bubble[]>(() => {
    const lines = useFactoryStore.getState().lines;
    const machines = useFactoryStore.getState().machines;

    const out: Bubble[] = [];
    // Take the latest 8 events and convert at-risk ones into messages.
    for (const e of events.slice(-8)) {
      const ts = e.isoDate;
      if (e.kind === "machine_reading" && e.payload.machineId) {
        const m = machines.find((x) => x.id === e.payload.machineId);
        if (m && (m.status === "at_risk" || m.status === "down")) {
          out.push({
            id: `${e.id}-in`,
            direction: "in",
            text:
              locale === "bn"
                ? `⚠️ ${m.id}: কম্পন ${m.vibration} mm/s, তাপমাত্রা ${m.temperature}°C`
                : `⚠️ ${m.id}: vibration ${m.vibration} mm/s, temperature ${m.temperature}°C`,
            timestamp: ts
          });
        }
      } else if (e.kind === "bundle_scan" && e.payload.lineId) {
        const l = lines.find((x) => x.id === e.payload.lineId);
        if (l && (l.status === "at_risk" || l.status === "down")) {
          const ePct = Math.round(l.efficiency * 100);
          const tPct = Math.round(l.targetEfficiency * 100);
          out.push({
            id: `${e.id}-in`,
            direction: "in",
            text:
              locale === "bn"
                ? `📉 ${l.name}: দক্ষতা ${bn(ePct)}% (লক্ষ্য ${bn(tPct)}%)`
                : `📉 ${l.name}: efficiency ${ePct}% vs target ${tPct}%`,
            timestamp: ts
          });
        }
      } else if (e.kind === "energy_meter") {
        out.push({
          id: `${e.id}-in`,
          direction: "in",
          text:
            locale === "bn"
              ? `🔌 শক্তি রিডিং: ${(e.payload.kwh as number)?.toFixed?.(1) ?? "—"} kWh`
              : `🔌 Energy reading: ${(e.payload.kwh as number)?.toFixed?.(1) ?? "—"} kWh`,
          timestamp: ts
        });
      }
    }
    // Append an "outbound" routing message to make it feel like a chat.
    out.push({
      id: "auto-ack",
      direction: "out",
      text:
        locale === "bn"
          ? "অর্কেস্ট্রেটর: রিলে তৈরি করছে — প্রাসঙ্গিক এজেন্টকে অবহিত করা হচ্ছে।"
          : "Orchestrator: preparing relay — routing the relevant agent now.",
      timestamp: new Date().toISOString()
    });
    return out.reverse();
  }, [events, locale]);

  if (!enabled) return null;

  const headerEvents = Math.max(bubbles.length - 1, 0);
  const headerCount = locale === "bn"
    ? `${headerEvents}টি ইভেন্ট`
    : `${headerEvents} events`;

  return (
    <section className="surface p-4" aria-label={t("whatsapp.alert.title") as string}>
      <header className="flex items-center justify-between gap-2">
        <div className="flex items-center gap-2 min-w-0">
          <span className="inline-flex size-7 items-center justify-center rounded-full bg-[#25D366]/20 border border-[#25D366]/40 text-[#1a8c4a] shrink-0">
            <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor" aria-hidden>
              <path d="M12 2C6.5 2 2 6.5 2 12c0 1.9.5 3.7 1.5 5.2L2 22l5-1.4c1.5.8 3.2 1.3 5 1.3 5.5 0 10-4.5 10-10S17.5 2 12 2zm5.6 14.1c-.2.6-1.3 1.2-1.8 1.3-.5.1-1 .1-3.3-.7-2.8-1-4.6-4.2-4.7-4.4-.1-.2-1.1-1.4-1.1-2.7 0-1.3.7-1.9 1-2.2.3-.3.6-.3.8-.3h.6c.2 0 .5-.1.7.5.2.6.8 2.1.9 2.3.1.2.1.4 0 .6-.1.2-.2.4-.4.5l-.3.4c-.2.2-.4.4-.2.8.3.4 1.1 1.8 2.4 2.9 1.6 1.5 3 2 3.4 2.2.4.2.7.2.9-.1.3-.3 1-1.2 1.3-1.6.3-.4.5-.3.9-.2.3.1 2.1 1 2.5 1.2.4.2.6.3.7.5.1.2.1.7-.1 1.3z" />
            </svg>
          </span>
          <div className="min-w-0">
            <p className="text-body text-fg-primary truncate">{t("whatsapp.alert.title")}</p>
            <p className="text-caption text-fg-tertiary truncate">{t("whatsapp.alert.subtitle") as string}</p>
          </div>
        </div>
        <span className="mono-pill text-fg-tertiary shrink-0">{headerCount}</span>
      </header>

      <div
        className="mt-3 h-72 overflow-y-auto rounded-md p-3 space-y-2"
        style={{ background: "linear-gradient(180deg, #ECE5DD 0%, #D9CBB7 100%)" }}
      >
        {bubbles.length <= 1 ? (
          <p className="text-caption text-fg-tertiary text-center mt-8">{t("whatsapp.alert.empty") as string}</p>
        ) : (
          <AnimatePresence initial={false}>
            {bubbles.map((b) => (
              <motion.div
                key={b.id}
                initial={{ opacity: 0, y: 6, scale: 0.96 }}
                animate={{ opacity: 1, y: 0, scale: 1 }}
                transition={{ duration: 0.2 }}
                className={
                  b.direction === "out"
                    ? "wp-bubble-out relative ml-auto max-w-[80%] bg-[#d9fdd3] px-3 py-2 rounded-md text-caption text-[#111b21] shadow"
                    : "wp-bubble-in relative mr-auto max-w-[80%] bg-surface px-3 py-2 rounded-md text-caption text-fg-primary shadow"
                }
              >
                {b.text}
              </motion.div>
            ))}
          </AnimatePresence>
        )}
      </div>
    </section>
  );
}

function bn(n: number) {
  const map: Record<number, string> = { 0: "০", 1: "১", 2: "২", 3: "৩", 4: "৪", 5: "৫", 6: "৬", 7: "৭", 8: "৮", 9: "৯" };
  return String(n).replace(/[0-9]/g, (d) => map[Number(d)]);
}
