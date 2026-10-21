// QA defect: the generated client reached by a relative path escapes the '@shared/api' name patterns (D2/D5).
import { EpisodeDto } from '../../../shared/api/models/episode';

export const keep = (dto: EpisodeDto): EpisodeDto => dto;
