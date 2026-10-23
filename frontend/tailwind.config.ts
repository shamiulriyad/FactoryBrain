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
        mono: ["JetBrains Mono", "ui-monospace", "monospace"]
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
