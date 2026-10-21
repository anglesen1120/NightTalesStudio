import { MAX_BACKOFF_MS, nextDelayMs } from './backoff';

describe('nextDelayMs', () => {
  it('walks the ladder 1, 2, 4, 8, 16, 30 s with no jitter at random() = 0', () => {
    const delays = [0, 1, 2, 3, 4, 5].map((attempt) => nextDelayMs(attempt, () => 0));
    expect(delays).toEqual([1000, 2000, 4000, 8000, 16_000, 30_000]);
  });

  it('caps the base at 30 s for every later attempt', () => {
    for (const attempt of [5, 6, 10, 100, 10_000]) expect(nextDelayMs(attempt, () => 0)).toBe(MAX_BACKOFF_MS);
  });

  it('adds jitter between 0 and 20 % of the base', () => {
    for (const [attempt, base] of [
      [0, 1000],
      [3, 8000],
      [7, 30_000],
    ] as const) {
      expect(nextDelayMs(attempt, () => 0)).toBe(base);
      expect(nextDelayMs(attempt, () => 1)).toBeCloseTo(base * 1.2, 6);
      expect(nextDelayMs(attempt, () => 0.5)).toBeCloseTo(base * 1.1, 6);
    }
  });

  it('treats a negative attempt as the first one', () => {
    expect(nextDelayMs(-3, () => 0)).toBe(1000);
  });
});
