export const motionTokens = {
  duration: {
    micro: 120,
    short: 180,
    base: 240,
    medium: 320,
    long: 400,
    page: 320
  },
  ease: {
    out: [0.22, 1, 0.36, 1] as [number, number, number, number],
    inOut: [0.4, 0, 0.2, 1] as [number, number, number, number],
    /** Snappy spring used for press feedback and small UI gestures. */
    spring: { type: "spring" as const, stiffness: 420, damping: 32 }
  }
};