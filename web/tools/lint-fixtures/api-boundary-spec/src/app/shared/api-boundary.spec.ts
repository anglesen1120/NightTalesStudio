// 10.1.4-T1: a spec may import the generated client to assert its types (D5 test exemption).
import type { ProblemDetails } from '@shared/api';

export const probe: ProblemDetails | undefined = undefined;
