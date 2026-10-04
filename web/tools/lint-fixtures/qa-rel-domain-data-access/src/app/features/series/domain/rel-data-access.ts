// QA defect: domain reaching its own data-access layer by a '../../series/...' relative path (D2).
import { SeriesRepository } from '../../series/data-access/series.repository';

export type Leak = SeriesRepository;
