import type { Provider } from '@angular/core';
import { MeRepository } from '@core/session';
import { MeHttpRepository } from '../session/data-access/me.http-repository';
import { HeaderEndRegistry } from './header-end.registry';
import { ShellStore } from './shell.store';

/** Component-scoped providers of `ShellComponent` (never `Route.providers`): the store, the header slot, the `/me` port. */
export function provideShell(): Provider[] {
  return [ShellStore, HeaderEndRegistry, { provide: MeRepository, useClass: MeHttpRepository }];
}
