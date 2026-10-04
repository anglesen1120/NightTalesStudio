// QA defect: domain importing core/ by relative path ('@core/**' is banned for domain, D2).
import { Session } from '../../../core/session/session';

export type Leak = Session;
