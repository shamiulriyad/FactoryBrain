"use client";

import { useId } from "react";
import { motion, useReducedMotion } from "framer-motion";

const breathe = { duration: 4.2, repeat: Infinity, ease: "easeInOut" as const };
const stitch = { duration: 1.8, repeat: Infinity, ease: "easeInOut" as const };

/**
 * Sewing-floor operator. Soft shapes, one slow breath, a gentle stitch.
 * Painted with its own colors so the dark rail cannot wash it out.
 */
export function WorkerMascot({ size = 220 }: { size?: number }) {
  const reduce = useReducedMotion();
  const raw = useId().replace(/:/g, "");
  const skin = `skin-${raw}`;
  const apron = `apron-${raw}`;
  const glow = `glow-${raw}`;

  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 160 160"
      aria-hidden
      className="overflow-visible"
    >
      <defs>
        <radialGradient id={glow} cx="50%" cy="45%" r="50%">
          <stop offset="0%" stopColor="#E7C98A" stopOpacity="0.55" />
          <stop offset="70%" stopColor="#E7C98A" stopOpacity="0" />
        </radialGradient>
        <linearGradient id={skin} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor="#F0C9A0" />
          <stop offset="100%" stopColor="#D7A574" />
        </linearGradient>
        <linearGradient id={apron} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor="#6FA394" />
          <stop offset="100%" stopColor="#3E7A6C" />
        </linearGradient>
      </defs>

      <motion.ellipse
        cx="80"
        cy="86"
        rx="54"
        ry="46"
        fill={`url(#${glow})`}
        animate={reduce ? undefined : { opacity: [0.55, 1, 0.55], scale: [0.96, 1.04, 0.96] }}
        transition={reduce ? undefined : breathe}
        style={{ transformOrigin: "80px 86px" }}
      />

      <motion.g
        animate={reduce ? undefined : { y: [0, -3.5, 0] }}
        transition={reduce ? undefined : breathe}
      >
        {/* Soft shadow */}
        <ellipse cx="82" cy="138" rx="36" ry="5" fill="#1B242C" opacity="0.12" />

        {/* Machine, rounded */}
        <rect x="78" y="108" width="58" height="16" rx="8" fill="#5C6E7C" />
        <rect x="96" y="78" width="14" height="34" rx="7" fill="#6D8090" />
        <rect x="90" y="72" width="30" height="14" rx="7" fill="#7E909E" />
        <rect x="84" y="114" width="36" height="5" rx="2.5" fill="#F6F1E8" />

        {/* Needle, slow */}
        <motion.g
          animate={reduce ? undefined : { y: [0, 8, 0] }}
          transition={reduce ? undefined : stitch}
        >
          <rect x="101" y="84" width="3.2" height="20" rx="1.6" fill="#E2B15A" />
          <path d="M102.6 102 L105.2 110 L100 110 Z" fill="#3E4C59" />
        </motion.g>

        {/* Thread being drawn */}
        <motion.path
          d="M103 110 C 112 118, 124 112, 132 122"
          fill="none"
          stroke="#E2B15A"
          strokeWidth="1.6"
          strokeLinecap="round"
          strokeDasharray="18 10"
          animate={reduce ? undefined : { strokeDashoffset: [0, -28] }}
          transition={reduce ? undefined : { duration: 2.4, repeat: Infinity, ease: "linear" }}
        />

        {/* Cloth as a soft wave */}
        <motion.path
          d="M82 118 C 96 114, 110 122, 124 116"
          fill="none"
          stroke="#F3E6C8"
          strokeWidth="4"
          strokeLinecap="round"
          animate={reduce ? undefined : { d: [
            "M82 118 C 96 114, 110 122, 124 116",
            "M82 118 C 96 122, 110 114, 124 120",
            "M82 118 C 96 114, 110 122, 124 116"
          ] }}
          transition={reduce ? undefined : { duration: 3.2, repeat: Infinity, ease: "easeInOut" }}
        />

        {/* Operator */}
        <motion.g
          style={{ transformOrigin: "62px 96px" }}
          animate={reduce ? undefined : { rotate: [-1.2, 1.6, -1.2] }}
          transition={reduce ? undefined : { duration: 4.8, repeat: Infinity, ease: "easeInOut" }}
        >
          <path
            d="M34 52 C34 30 50 22 64 28 C76 18 96 28 94 50 C100 60 92 74 76 74 C54 78 34 68 34 52 Z"
            fill="#E2B15A"
          />
          <path
            d="M88 46 C100 56 98 78 86 74"
            fill="none"
            stroke="#C4923A"
            strokeWidth="4"
            strokeLinecap="round"
          />
          <motion.path
            d="M88 46 C100 56 98 78 86 74"
            fill="none"
            stroke="#C4923A"
            strokeWidth="4"
            strokeLinecap="round"
            style={{ transformOrigin: "90px 50px" }}
            animate={reduce ? undefined : { rotate: [0, 6, 0] }}
            transition={reduce ? undefined : { duration: 3.6, repeat: Infinity, ease: "easeInOut" }}
          />

          <circle cx="62" cy="52" r="16" fill={`url(#${skin})`} />
          <path d="M52 50 Q62 54 72 50" fill="none" stroke="#8A6238" strokeWidth="1.2" strokeLinecap="round" opacity="0.7" />
          <circle cx="56" cy="50" r="1.3" fill="#3E342C" />
          <circle cx="68" cy="50" r="1.3" fill="#3E342C" />

          <path
            d="M40 72 C40 64 50 60 64 60 C80 60 90 66 90 78 L86 118 C70 124 48 124 36 116 Z"
            fill={`url(#${apron})`}
          />
          <path d="M50 78 H76 C74 108 70 116 62 116 C52 116 48 108 50 78 Z" fill="#F7F3EA" />

          <motion.g
            style={{ transformOrigin: "78px 84px" }}
            animate={reduce ? undefined : { rotate: [0, -6, 0] }}
            transition={reduce ? undefined : stitch}
          >
            <path
              d="M78 84 C92 86 100 96 104 104"
              fill="none"
              stroke="#D7A574"
              strokeWidth="7"
              strokeLinecap="round"
            />
            <circle cx="104" cy="104" r="3.2" fill="#F0C9A0" />
          </motion.g>
        </motion.g>
      </motion.g>
    </svg>
  );
}

/** Drop-in name kept so existing call sites pick up the operator. */
export function Brain3D({ size = 220 }: { size?: number }) {
  return <WorkerMascot size={size} />;
}
