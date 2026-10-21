/** Longest base delay of the reconnect ladder (README D8). */
export const MAX_BACKOFF_MS = 30_000;

/** Jitter added on top of the base, as a share of it. */
const JITTER_SHARE = 0.2;

/**
 * Delay before reconnect attempt number `attempt` (0-based): `min(30 s, 1 s · 2^attempt)` — 1, 2, 4, 8, 16, 30, 30 … —
 * plus `random() · 20 %` of the base. `random` yields `[0, 1)`; it is a parameter so specs are deterministic.
 */
export function nextDelayMs(attempt: number, random: () => number): number {
  const exponent = Math.min(Math.max(0, Math.trunc(attempt)), 5);
  const base = Math.min(MAX_BACKOFF_MS, 1000 * 2 ** exponent);
  return base + random() * JITTER_SHARE * base;
}
