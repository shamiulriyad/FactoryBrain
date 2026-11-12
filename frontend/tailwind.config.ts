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
        // Brand + risk — bound to vars
        accent: "var(--accent)",
        risk: {
          low: "var(--risk-low)",
          medium: "var(--risk-medium)",
          high: "var(--risk-high)"
        },
        // App sidebar (always dark, independent of the page theme).
        sb: {
          bg: "#0d1218",
          input: "#121820",
          border: "#232b36",
          divider: "#1f2731",
          active: "#1d2531",
          hover: "#161d26",
          text: "#ece7df",
          dim: "#c3c7cc",
          muted: "#a4a9af",
          label: "#8d949e",
          accent: "#f2c27a",
          ok: "#8fc9a8",
          watch: "#f2c27a",
          act: "#ff6b45",
          ink: "#1a1308"
        },
        // FactoryBrain marketing homepage (single dark theme).
        fb: {
          ink: "#0f141b",
          text: "#ece7df",
          muted: "#a4a9af",
          line: "rgba(236,231,223,0.14)",
          thread: "#ff6b45",
          lamp: "#f2c27a",
          ok: "#8fc9a8",
          card: "rgba(14,19,26,0.86)"
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
        card: "0 1px 0 inset rgba(255,255,255,0.04), 0 1px 2px rgba(0,0,0,0.4)",
        glass: "0 24px 48px rgba(0,0,0,0.4)",
        pop: "0 1px 0 inset rgba(255,255,255,0.06), 0 8px 24px rgba(0,0,0,0.4)"
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
