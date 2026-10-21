// `bundled-translations` is deliberately not re-exported: it statically imports the scope JSON, and only the
// loader's server branch may pull it in (dynamic import), so the browser's initial chunk stays free of it.
export * from './locale.model';
export * from './locale.service';
export * from './transloco.config';
