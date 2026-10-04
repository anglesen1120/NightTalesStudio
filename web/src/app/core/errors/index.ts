// `data-access/problem-wire` is the one file that imports the generated client (lint D2/D5); consumers outside
// `data-access` take the `ProblemWire` type from here.
export * from './app-error';
export * from './app-error-mapper';
export * from './bind-field-errors';
export * from './error-code';
export type { ProblemWire } from './data-access/problem-wire';
