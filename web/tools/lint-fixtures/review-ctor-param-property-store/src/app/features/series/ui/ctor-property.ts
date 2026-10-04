// Review F-2: constructor parameter-property injection of a store in presentational ui.
class SeriesStore {}

export class CtorProperty {
  constructor(private readonly store: SeriesStore) {}

  read(): SeriesStore {
    return this.store;
  }
}
