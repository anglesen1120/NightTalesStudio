// QA defect: deep import into another feature by relative path (D2: only through its index.ts).
import { EpisodeCard } from '../../episodes/ui/episode-card';

export const card = EpisodeCard;
