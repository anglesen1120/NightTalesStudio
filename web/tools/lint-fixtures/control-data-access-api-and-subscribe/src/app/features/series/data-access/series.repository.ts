import type { SeriesDto } from '@shared/api';
import { of } from 'rxjs';

export class SeriesRepository {
  load(): SeriesDto | undefined {
    of(1).subscribe();
    return undefined;
  }
}
