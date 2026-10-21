// Review F-3a: computed access to eval.
export const run = (code: string): unknown => globalThis['eval'](code);
