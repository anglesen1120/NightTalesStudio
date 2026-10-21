// Review F-2: plain constructor parameter of a repository type in presentational ui.
class SeriesRepository {}

export class CtorPlain {
  private readonly repository: SeriesRepository;

  constructor(repository: SeriesRepository) {
    this.repository = repository;
  }

  read(): SeriesRepository {
    return this.repository;
  }
}
