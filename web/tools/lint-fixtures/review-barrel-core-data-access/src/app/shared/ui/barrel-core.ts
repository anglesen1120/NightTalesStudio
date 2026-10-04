// Review F-1: the bare @core/<x>/data-access alias (barrel) from shared ui.
import { session } from '@core/auth/data-access';

export const run = session;
