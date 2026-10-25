"use client";

import { useEffect, useState } from "react";
import { Building2, Palette, Database, Trash2, Sun, Moon, Monitor, Users, Cpu } from "lucide-react";
import { useRouter, useSearchParams } from "next/navigation";
import { useT } from "@/lib/useT";
import { useBusinessStore } from "@/store/business.store";
import { useAppStore, type Theme } from "@/store/app.store";
import { Panel } from "@/components/ui/Panel";
import { PageHeader } from "@/components/ui/PageHeader";
import { Input } from "@/components/ui/Input";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { ConfirmDialog } from "@/components/ui/Modal";
import { LanguageToggle } from "@/components/ui/LanguageToggle";
import { LlmSettingsPanel } from "@/components/settings/LlmSettingsPanel";
import { EmbeddingSettingsPanel } from "@/components/settings/EmbeddingSettingsPanel";
import { MyAccessPanel, UsersPanel } from "@/components/settings/UsersPanel";
import { cn } from "@/lib/cn";
import { isAdmin, useAuthStore } from "@/lib/auth";

// "advanced" keeps its id so existing deep links (?section=advanced) still open the AI section.
type SectionId = "profile" | "appearance" | "advanced" | "users" | "dataReset";

const SECTIONS: Array<{ id: SectionId; icon: typeof Building2; labelKey: string; adminOnly?: boolean; hint: string }> = [
  { id: "advanced", icon: Cpu, labelKey: "settings.aiModels", hint: "Language model & embeddings" },
  { id: "users", icon: Users, labelKey: "settings.usersSection", hint: "Roles and access" },
  { id: "profile", icon: Building2, labelKey: "settings.businessProfile", hint: "This browser" },
  { id: "appearance", icon: Palette, labelKey: "settings.appearance", hint: "Theme, language" },
  { id: "dataReset", icon: Database, labelKey: "settings.dataReset", hint: "This browser" }
];

export default function SettingsPage() {
  const { t } = useT();
  const router = useRouter();
  const search = useSearchParams();
  const sectionParam = search?.get("section") as SectionId | null;
  const [active, setActive] = useState<SectionId>("advanced");
  const admin = isAdmin(useAuthStore((s) => s.user));
  const sections = SECTIONS.filter((s) => !s.adminOnly || admin);

  // Honor ?section= deep links from the command palette.
  useEffect(() => {
    if (sectionParam && sections.some((s) => s.id === sectionParam)) setActive(sectionParam);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sectionParam, admin]);

  function onSelectSection(id: SectionId) {
    setActive(id);
    const url = new URL(window.location.href);
    url.searchParams.set("section", id);
    window.history.replaceState({}, "", url.toString());
  }

  return (
    <div className="page" data-tour="settings">
      <PageHeader eyebrow="Administration" title={t("settings.title")} description={t("settings.subtitle")} />

      <div className="mt-6 grid grid-cols-1 md:grid-cols-[220px_minmax(0,1fr)] gap-6">
        <nav aria-label="Settings sections" className="md:sticky md:top-4 md:self-start">
          <ul className="flex md:flex-col gap-1 overflow-x-auto md:overflow-visible -mx-1 px-1 pb-1">
            {sections.map((s) => (
              <li key={s.id} className="shrink-0">
                <button
                  type="button"
                  onClick={() => onSelectSection(s.id)}
                  aria-current={active === s.id ? "page" : undefined}
                  className={cn(
                    "flex items-center gap-2.5 w-full px-3 py-2 rounded-[var(--radius-control)] text-left whitespace-nowrap transition-colors border",
                    active === s.id
                      ? "bg-[var(--bg-surface)] border-border-subtle shadow-sm text-fg-primary"
                      : "border-transparent text-fg-secondary hover:text-fg-primary hover:bg-surface-2"
                  )}
                >
                  <s.icon size={15} className={active === s.id ? "text-accent" : undefined} />
                  <span className="min-w-0">
                    <span className="block text-body font-medium">{t(s.labelKey)}</span>
                    <span className="hidden md:block text-[11px] text-fg-tertiary">{s.hint}</span>
                  </span>
                </button>
              </li>
            ))}
          </ul>
        </nav>

        <div className="space-y-4 min-w-0">
          {active === "advanced" && (
            <>
              <LlmSettingsPanel />
              <EmbeddingSettingsPanel />
            </>
          )}
          {active === "users" && (admin ? <UsersPanel /> : <MyAccessPanel />)}
          {active === "profile" && <ProfileSection />}
          {active === "appearance" && <AppearanceSection />}
          {active === "dataReset" && <DataResetSection router={router} />}
        </div>
      </div>
    </div>
  );
}

function Row({ label, hint, children }: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <div className="grid grid-cols-1 sm:grid-cols-[220px_1fr] gap-2 sm:gap-4 py-3 border-b border-border-subtle last:border-b-0">
      <div>
        <p className="text-body font-medium text-fg-primary">{label}</p>
        {hint && <p className="mt-0.5 text-caption text-fg-tertiary">{hint}</p>}
      </div>
      <div className="min-w-0">{children}</div>
    </div>
  );
}

const LOCAL_NOTE = "Saved in this browser only; not sent to the server and not shared with other users.";

function ProfileSection() {
  const { t } = useT();
  const profile = useBusinessStore((s) => s.profile);
  const setProfile = useBusinessStore((s) => s.setProfile);
  return (
    <Panel title={t("settings.businessProfile")} subtitle={LOCAL_NOTE}>
      <Row label={t("settings.businessName")} hint="Shown in greetings on this device">
        <Input value={profile.businessName} onChange={(e) => setProfile({ businessName: e.target.value })} aria-label={t("settings.businessName")} />
      </Row>
      <Row label={t("settings.city")}>
        <Input value={profile.city} onChange={(e) => setProfile({ city: e.target.value })} aria-label={t("settings.city")} />
      </Row>
      <p className="mt-3 text-caption text-fg-tertiary">Changes save as you type.</p>
    </Panel>
  );
}

function AppearanceSection() {
  const { t } = useT();
  const theme = useAppStore((s) => s.theme);
  const setTheme = useAppStore((s) => s.setTheme);
  const options: Array<{ v: Theme; Icon: typeof Sun; label: string }> = [
    { v: "light", Icon: Sun, label: t("settings.themeLight") },
    { v: "dark", Icon: Moon, label: t("settings.themeDark") },
    { v: "system", Icon: Monitor, label: t("settings.themeSystem") }
  ];
  return (
    <Panel title={t("settings.appearance")} subtitle={LOCAL_NOTE}>
      <Row label={t("settings.language")}>
        <LanguageToggle />
      </Row>
      <Row label={t("settings.theme")} hint="Light, dark, or follow your system">
        <div className="inline-flex rounded-[var(--radius-control)] border border-border-subtle p-0.5 bg-surface-2" role="radiogroup" aria-label={t("settings.theme")}>
          {options.map((o) => (
            <button
              key={o.v}
              type="button"
              role="radio"
              aria-checked={theme === o.v}
              onClick={() => setTheme(o.v)}
              className={cn(
                "h-8 px-3 rounded-md inline-flex items-center gap-1.5 text-caption transition-colors",
                theme === o.v ? "bg-[var(--bg-surface)] text-fg-primary shadow-sm" : "text-fg-secondary hover:text-fg-primary"
              )}
            >
              <o.Icon size={13} /> {o.label}
            </button>
          ))}
        </div>
      </Row>
    </Panel>
  );
}

function DataResetSection({ router }: { router: ReturnType<typeof useRouter> }) {
  const { t } = useT();
  const resetDemo = useAppStore((s) => s.resetDemo);
  const [confirmReset, setConfirmReset] = useState(false);

  return (
    <Panel title={t("settings.dataReset")} subtitle={t("settings.dataResetBody")}>
      <Callout tone="info" className="mb-3">
        Only this browser&apos;s saved preferences and demo data are removed. Approvals, documents, AI settings and users on the server are not affected, and you stay signed in.
      </Callout>
      <Row label={t("settings.resetDemo")} hint={t("settings.resetDemoHint")}>
        <Button variant="danger" size="sm" onClick={() => setConfirmReset(true)}>
          <Trash2 size={13} /> {t("settings.resetDemo")}
        </Button>
      </Row>
      <ConfirmDialog
        open={confirmReset}
        title={t("settings.resetConfirm")}
        description="Profile, theme, language, Ask history and demo integrations in this browser are cleared."
        confirmLabel={t("common.confirm")}
        tone="danger"
        onConfirm={() => {
          resetDemo();
          router.replace("/onboarding/welcome");
        }}
        onCancel={() => setConfirmReset(false)}
      />
    </Panel>
  );
}
