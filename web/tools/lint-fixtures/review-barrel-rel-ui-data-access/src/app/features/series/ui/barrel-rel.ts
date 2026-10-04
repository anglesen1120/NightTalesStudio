// Review F-1: a relative import of the bare data-access directory (its barrel index.ts) from presentational ui.
import { load } from '../data-access';

export const run = load;
