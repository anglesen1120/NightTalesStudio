import { clamp } from '@shared/util/clamp';
import { Sibling } from './sibling';

export const seriesTitle = (value: string): string => clamp(value) + String(Sibling);
