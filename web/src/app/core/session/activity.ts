import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import { DestroyRef, Injectable, Injector, PLATFORM_ID, computed, inject, signal } from '@angular/core';
import { SessionStore } from './session-store';

/** Idle windows (README D7): just below the server limits (C1) — owner/admin 25 min, user 13 days. */
export const ACTIVITY_WINDOWS = { admin: 25 * 60_000, user: 13 * 86_400_000 } as const;

/** At most one `lastInputAt` write per this many ms. */
export const ACTIVITY_THROTTLE_MS = 100;

/** Re-evaluates `isActive` for effects while the tracker runs. */
const TICK_INTERVAL_MS = 10_000;

const INPUT_EVENTS = ['pointerdown', 'keydown', 'wheel', 'touchstart'] as const;

/**
 * Tracks user input and tab visibility so background work (hub reconnect, polling) refreshes the session only while
 * the user is around (README D7, WR11). The role comes lazily from `SessionStore` through the injector: the store
 * depends on this tracker, so a direct injection would be a cycle.
 */
@Injectable({ providedIn: 'root' })
export class ActivityTracker {
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  private readonly platformId = inject(PLATFORM_ID);

  // The page load itself counts as the first input, so a freshly booted tab is active until its window elapses.
  private readonly lastInputState = signal(Date.now());
  private readonly visibleState = signal(this.document.visibilityState !== 'hidden');
  private readonly tick = signal(0);

  readonly lastInputAt = this.lastInputState.asReadonly();
  readonly isVisible = this.visibleState.asReadonly();
  readonly idleWindowMs = computed(() =>
    this.injector.get(SessionStore).role() === 'user' ? ACTIVITY_WINDOWS.user : ACTIVITY_WINDOWS.admin,
  );

  private intervalId: number | null = null;
  private started = false;

  private readonly onInput = (): void => {
    const now = Date.now();
    if (now - this.lastInputState() >= ACTIVITY_THROTTLE_MS) this.lastInputState.set(now);
  };

  private readonly onVisibility = (): void => {
    this.visibleState.set(this.document.visibilityState !== 'hidden');
  };

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.stop();
    });
  }

  /**
   * Input within the idle window. A function over signals rather than a `computed`: the clock is not a signal, so
   * a cached value would stay `true` while the tracker is stopped; reading `tick` keeps effects reactive while it runs.
   */
  isActive(): boolean {
    this.tick();
    return Date.now() - this.lastInputState() < this.idleWindowMs();
  }

  start(): void {
    const view = this.document.defaultView;
    if (this.started || !isPlatformBrowser(this.platformId) || view === null) return;
    this.started = true;
    for (const name of INPUT_EVENTS) view.addEventListener(name, this.onInput, { passive: true });
    this.document.addEventListener('visibilitychange', this.onVisibility);
    this.onVisibility();
    this.intervalId = view.setInterval(() => {
      this.tick.update((n) => n + 1);
    }, TICK_INTERVAL_MS);
  }

  stop(): void {
    if (!this.started) return;
    this.started = false;
    const view = this.document.defaultView;
    if (view !== null) {
      for (const name of INPUT_EVENTS) view.removeEventListener(name, this.onInput);
      if (this.intervalId !== null) view.clearInterval(this.intervalId);
    }
    this.document.removeEventListener('visibilitychange', this.onVisibility);
    this.intervalId = null;
  }
}
