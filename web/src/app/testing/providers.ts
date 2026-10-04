import { provideZonelessChangeDetection } from '@angular/core';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

/**
 * The single provider list every spec spreads into `TestBed.configureTestingModule` (README D15):
 * zoneless change detection, and Material animations off.
 */
export const ZONELESS_TEST_PROVIDERS = [provideZonelessChangeDetection(), provideNoopAnimations()];
