import { InjectionToken, type Type } from '@angular/core';
import { IconEpisodes } from '@shared/ui';

/** One side-navigation destination. `labelKey` is a Transloco key; `route` is an absolute path. */
export interface NavItem {
  readonly id: string;
  readonly labelKey: string;
  readonly route: string;
  readonly icon: Type<unknown>;
  readonly visible: () => boolean;
}

/**
 * Each screen task appends its entry together with its child route in the same PR (README D14, task 10.2.6-T1).
 * Never add a temporary or stub entry. Labels are `core` keys: the shell loads only the `core` scope.
 */
export const NAV_ITEMS: readonly NavItem[] = [
  { id: 'episodes', labelKey: 'shell.nav.episodes', route: '/episodes', icon: IconEpisodes, visible: () => true },
];

/** Specs provide entries through this token; production resolves `NAV_ITEMS`. */
export const NAV_ITEMS_TOKEN = new InjectionToken<readonly NavItem[]>('NAV_ITEMS', { factory: () => NAV_ITEMS });
