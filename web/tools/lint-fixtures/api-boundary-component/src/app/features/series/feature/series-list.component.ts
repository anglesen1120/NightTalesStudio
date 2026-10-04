// 10.1.4-T1: a component must not reach the generated client; only data-access may (D2/D5).
import type { ProblemDetails } from '@shared/api';

export type ListError = ProblemDetails;
