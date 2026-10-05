import { InjectionToken, type Type } from '@angular/core';

/** One side-navigation destination. `labelKey` is a Transloco key; `route` is an absolute path. */
export interface NavItem {
  readonly id: string;
  readonly labelKey: string;
  readonly route: string;
  readonly icon: Type<unknown>;
  readonly visible: () => boolean;
}

/**
 * Empty on purpose: each screen task appends its entry together with its child route in the same PR (README D14,
 * task 10.2.6-T1). Never add a temporary or stub entry.
 */
export const NAV_ITEMS: readonly NavItem[] = [];

/** Specs provide entries through this token; production resolves `NAV_ITEMS`. */
export const NAV_ITEMS_TOKEN = new InjectionToken<readonly NavItem[]>('NAV_ITEMS', { factory: () => NAV_ITEMS });
