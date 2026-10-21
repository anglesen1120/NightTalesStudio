// Review F-3a: computed access to Function.
export const make = (body: string): unknown => window['Function'](body);
