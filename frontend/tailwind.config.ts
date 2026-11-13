import type { Config } from "tailwindcss";

const config: Config = {
  content: ["./src/**/*.{ts,tsx}"],
  darkMode: "class",
  theme: {
    extend: {
      colors: {
        // Surfaces — map to CSS vars so theme switching is free
        canvas: "var(--bg-canvas)",
        sidebar: "var(--bg-sidebar)",
        surface: "var(--bg-surface)",
        "surface-2": "var(--bg-surface-2)",
        "surface-3": "var(--bg-surface-3)",
        info: "var(--info)",
        glass: "var(--bg-glass)",
        // Borders
        border: {
          subtle: "var(--border-subtle)",
          strong: "var(--border-strong)"
        },
        // Text tiers
        fg: {
          primary: "var(--fg-primary)",
          secondary: "var(--fg-secondary)",
          tertiary: "var(--fg-tertiary)"
        },
        // Brand palette (src/app/globals.css → --brand-*). Use these, never raw hex.
        brand: {
          navy: "rgb(var(--brand-navy) / <alpha-value>)",
          "navy-deep": "rgb(var(--brand-navy-deep) / <alpha-value>)",
          emerald: "rgb(var(--brand-emerald) / <alpha-value>)",
          "emerald-light": "rgb(var(--brand-emerald-light) / <alpha-value>)",
          "emerald-ink": "rgb(var(--brand-emerald-ink) / <alpha-value>)",
          white: "rgb(var(--brand-white) / <alpha-value>)",
          offwhite: "rgb(var(--brand-offwhite) / <alpha-value>)",
          border: "rgb(var(--brand-border) / <alpha-value>)",
          text: "rgb(var(--brand-text) / <alpha-value>)",
          body: "rgb(var(--brand-body) / <alpha-value>)",
          muted: "rgb(var(--brand-muted) / <alpha-value>)",
          success: "rgb(var(--brand-success) / <alpha-value>)",
          warning: "rgb(var(--brand-warning) / <alpha-value>)",
          error: "rgb(var(--brand-error) / <alpha-value>)",
          info: "rgb(var(--brand-info) / <alpha-value>)"
        },
        // Theme accent + risk — bound to vars (accent supports opacity: bg-accent/15)
        accent: "rgb(var(--accent-rgb) / <alpha-value>)",
        risk: {
          low: "var(--risk-low)",
          medium: "var(--risk-medium)",
          high: "var(--risk-high)"
        },
        // App sidebar (always deep navy, independent of the page theme) → --sb-* tokens.
        sb: {
          bg: "rgb(var(--sb-bg) / <alpha-value>)",
          input: "rgb(var(--sb-input) / <alpha-value>)",
          border: "rgb(var(--sb-border) / <alpha-value>)",
          divider: "rgb(var(--sb-divider) / <alpha-value>)",
          active: "rgb(var(--sb-active) / <alpha-value>)",
          hover: "rgb(var(--sb-hover) / <alpha-value>)",
          text: "rgb(var(--sb-text) / <alpha-value>)",
          dim: "rgb(var(--sb-dim) / <alpha-value>)",
          muted: "rgb(var(--sb-muted) / <alpha-value>)",
          label: "rgb(var(--sb-label) / <alpha-value>)",
          accent: "rgb(var(--sb-accent) / <alpha-value>)",
          ok: "rgb(var(--sb-ok) / <alpha-value>)",
          watch: "rgb(var(--sb-watch) / <alpha-value>)",
          act: "rgb(var(--sb-act) / <alpha-value>)",
          ink: "rgb(var(--sb-ink) / <alpha-value>)"
        },
        // Marketing homepage → --home-* tokens (navy by default; .fb-light / .fb-offwhite flip them).
        fb: {
          ink: "rgb(var(--home-ink) / <alpha-value>)",
          text: "rgb(var(--home-text) / <alpha-value>)",
          muted: "rgb(var(--home-muted) / <alpha-value>)",
          line: "var(--home-line)",
          thread: "rgb(var(--home-thread) / <alpha-value>)",
          lamp: "rgb(var(--home-lamp) / <alpha-value>)",
          warn: "rgb(var(--home-warn) / <alpha-value>)",
          ok: "rgb(var(--home-ok) / <alpha-value>)",
          card: "var(--home-card)",
          field: "var(--home-field)"
        },
        stage: {
          suggested: "var(--stage-suggested)",
          pending: "var(--stage-pending)",
          executing: "var(--stage-executing)",
          done: "var(--stage-done)",
          logged: "var(--stage-logged)"
        }
      },
      fontFamily: {
        sans: ["Inter", "Hind Siliguri", "system-ui", "sans-serif"],
        bn: ["Hind Siliguri", "sans-serif"],
        mono: ["JetBrains Mono", "ui-monospace", "monospace"],
        display: ["var(--font-display)", "Impact", "sans-serif"],
        plex: ["var(--font-plex)", "system-ui", "sans-serif"],
        plexmono: ["var(--font-plex-mono)", "ui-monospace", "monospace"]
      },
      fontSize: {
        display: ["28px", { lineHeight: "34px", fontWeight: "600" }],
        title: ["18px", { lineHeight: "24px", fontWeight: "560" }],
        body: ["14px", { lineHeight: "20px", fontWeight: "460" }],
        caption: ["12px", { lineHeight: "16px", fontWeight: "460" }]
      },
      spacing: {
        "1": "4px",
        "1.5": "6px",
        "2": "8px",
        "2.5": "10px",
        "3": "12px",
        "4": "16px",
        "6": "24px",
        "8": "32px",
        "12": "48px"
      },
      borderRadius: {
        DEFAULT: "6px",
        sm: "4px",
        md: "8px",
        lg: "10px",
        xl: "12px"
      },
      boxShadow: {
        card: "var(--shadow-card)",
        glass: "var(--glass-shadow)",
        pop: "var(--shadow-pop)"
      },
      backdropBlur: {
        md: "12px"
      },
      transitionTimingFunction: {
        emphasis: "cubic-bezier(0.22, 1, 0.36, 1)"
      }
    }
  },
  plugins: []
};

export default config;
