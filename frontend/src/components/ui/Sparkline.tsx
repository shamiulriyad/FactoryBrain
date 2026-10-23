"use client";

import { useId } from "react";
import { motion } from "framer-motion";
import { motionTokens } from "@/lib/motion";

type Props = {
  values: number[];
  width?: number;
  height?: number;
  stroke?: string;
};

export function Sparkline({ values, width = 120, height = 32, stroke = "#7CE0C6" }: Props) {
  const id = useId();
  if (!values.length) return <div style={{ width, height }} />;
  const min = Math.min(...values);
  const max = Math.max(...values);
  const range = Math.max(1, max - min);
  const step = values.length > 1 ? width / (values.length - 1) : 0;
  const points = values
    .map((v, i) => `${(i * step).toFixed(2)},${(height - ((v - min) / range) * height).toFixed(2)}`)
    .join(" ");
  const last = values[values.length - 1];
  const lastX = (values.length - 1) * step;
  const lastY = height - ((last - min) / range) * height;
  return (
    <svg width={width} height={height} viewBox={`0 0 ${width} ${height}`}>
      <motion.polyline
        initial={{ pathLength: 0, opacity: 0 }}
        animate={{ pathLength: 1, opacity: 1 }}
        transition={{ duration: motionTokens.duration.medium / 1000, ease: motionTokens.ease.out }}
        points={points}
        fill="none"
        stroke={stroke}
        strokeWidth={1.5}
        strokeLinejoin="round"
        strokeLinecap="round"
      />
      <motion.circle
        initial={{ scale: 0 }}
        animate={{ scale: 1 }}
        transition={{ delay: 0.3, duration: 0.2 }}
        cx={lastX}
        cy={lastY}
        r={2.5}
        fill={stroke}
      />
      <title>{`Sparkline ${id}`}</title>
    </svg>
  );
}