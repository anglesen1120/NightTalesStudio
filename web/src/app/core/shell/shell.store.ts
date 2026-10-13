import { DestroyRef, Injectable, Injector, computed, effect, inject, signal, untracked, type Signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Title } from '@angular/platform-browser';
import { ActivatedRouteSnapshot, NavigationEnd, Router } from '@angular/router';
import { TranslocoService } from '@jsverse/transloco';
import { filter, firstValueFrom } from 'rxjs';
import { AppErrorMapper, type AppError } from '@core/errors';
import { LocaleService, type AppLocale } from '@core/i18n';
import { RealtimeService, type RealtimeState } from '@core/realtime';
import { MeRepository, SessionStore, type MeSummary } from '@core/session';
import { NAV_ITEMS_TOKEN, type NavItem } from './nav-items';
import { UnsavedWorkRegistry } from './unsaved-work';

const SNACKBAR_DURATION_MS = 6_000;

/**
 * The browser tab title the document shipped with (`index.html`): the product name. There is no product-name copy key in
 * the `core` scope, so a route without `data.titleKey` falls back to this value rather than an invented key. Captured
 * once so a later shell instance never mistakes a page title for the product name.
 */
let productName: string | null = null;

function isAppError(value: unknown): value is AppError {
  return typeof value === 'object' && value !== null && 'code' in value && 'status' in value && 'kind' in value;
}

/** The `data.titleKey` of the deepest activated route that declares one. */
function deepestTitleKey(root: ActivatedRouteSnapshot): string | null {
  let found: string | null = null;
  for (let route: ActivatedRouteSnapshot | null = root; route !== null; route = route.firstChild) {
    const key: unknown = route.data['titleKey'];
    if (typeof key === 'string') found = key;
  }
  return found;
}

/**
 * Shell state (task 10.2.6-T1): the profile load, the language switch, the document/page title, logout and the realtime
 * resume. Provided by `ShellComponent` (component scope, never `Route.providers`), so the component's `DestroyRef` ends
 * it. Every async path captures the session `generation()` plus its own request token and compares both after each
 * `await` before writing `setMe`, the snackbar, the title or the error; destroy bumps every token, so a late result of
 * a gone session, another identity or a destroyed shell is discarded.
 */
@Injectable()
export class ShellStore {
  private readonly meRepository = inject(MeRepository);
  private readonly session = inject(SessionStore);
  private readonly localeService = inject(LocaleService);
  private readonly realtime = inject(RealtimeService);
  private readonly snack = inject(MatSnackBar);
  private readonly title = inject(Title);
  private readonly transloco = inject(TranslocoService);
  private readonly router = inject(Router);
  private readonly mapper = inject(AppErrorMapper);
  private readonly injector = inject(Injector);
  private readonly navItemsSource = inject(NAV_ITEMS_TOKEN);
  private readonly unsavedWork = inject(UnsavedWorkRegistry);

  readonly summary: Signal<MeSummary | null> = computed(() => this.session.me());
  readonly navItems: Signal<readonly NavItem[]> = computed(() => this.navItemsSource.filter((item) => item.visible()));
  readonly realtimeState: Signal<RealtimeState> = computed(() => this.realtime.state());
  readonly locale: Signal<AppLocale> = computed(() => this.localeService.locale());

  private readonly pageTitleState = signal('');
  readonly pageTitle = this.pageTitleState.asReadonly();
  private readonly profileErrorState = signal<AppError | null>(null);
  readonly profileError = this.profileErrorState.asReadonly();
  private readonly localeSavingState = signal(false);
  readonly localeSaving = this.localeSavingState.asReadonly();

  private destroyed = false;
  private meFlight: { readonly promise: Promise<void>; readonly generation: number } | null = null;
  private meToken = 0;
  private localeToken = 0;
  private titleToken = 0;
  /** Counts the user's own `changeLocale` choices; a profile locale never overwrites one made after the shell started. */
  private localChoices = 0;
  private appliedProfile: MeSummary | null = null;
  private loggingOut = false;
  private started = false;

  constructor() {
    productName ??= this.title.getTitle();
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.meToken += 1;
      this.localeToken += 1;
      this.titleToken += 1;
      this.meFlight = null;
    });
  }

  /** Called once by the `ShellComponent` constructor: title tracking, profile-locale sync, then the first `loadMe()`. */
  init(): void {
    if (this.started) return;
    this.started = true;
    const injector = this.injector;
    const navigated = toSignal(this.router.events.pipe(filter((event) => event instanceof NavigationEnd)), {
      initialValue: null,
      injector,
    });
    effect(
      () => {
        navigated();
        this.localeService.locale();
        untracked(() => void this.applyTitle());
      },
      { injector },
    );
    // README D10: the profile `uiLocale` becomes the language once `me()` is set, unless the user already chose one here.
    effect(
      () => {
        const me = this.session.me();
        untracked(() => {
          if (me === null || me === this.appliedProfile) return;
          this.appliedProfile = me;
          if (this.localChoices === 0 && me.uiLocale !== this.localeService.locale()) {
            this.localeService.activate(me.uiLocale, { persist: false });
          }
        });
      },
      { injector },
    );
    // The first attempt is immediate; the effect retries whenever the session becomes eligible later (e.g. it was
    // `refreshing` at mount). A failed load sets `profileError`, which keeps the effect quiet until `retryProfile()`.
    void this.loadMe();
    effect(
      () => {
        const eligible = this.session.mode() === 'full' && this.session.status() === 'authenticated';
        this.session.generation();
        const missing = this.session.me() === null;
        const failed = this.profileErrorState() !== null;
        if (eligible && missing && !failed) untracked(() => void this.loadMe());
      },
      { injector },
    );
  }

  /**
   * One guarded flight: only for a full authenticated session without a profile yet. Concurrent callers share the
   * Promise. A 401 follows the session policy (the interceptor owns it); any other failure only sets `profileError`.
   */
  loadMe(): Promise<void> {
    if (this.destroyed || this.session.mode() !== 'full' || this.session.status() !== 'authenticated' || this.session.me() !== null) {
      return Promise.resolve();
    }
    const generation = this.session.generation();
    if (this.meFlight !== null && this.meFlight.generation === generation) return this.meFlight.promise;
    this.meToken += 1;
    const token = this.meToken;
    const promise = this.fetchMe(generation, token).finally(() => {
      if (this.meFlight?.promise === promise) this.meFlight = null;
    });
    this.meFlight = { promise, generation };
    return promise;
  }

  retryProfile(): Promise<void> {
    this.profileErrorState.set(null);
    return this.loadMe();
  }

  /** The language is applied locally at once; the profile is persisted only for a full authenticated session. */
  async changeLocale(locale: AppLocale): Promise<void> {
    if (this.destroyed || this.localeSavingState()) return;
    this.localChoices += 1;
    this.localeService.activate(locale, { persist: true });
    if (this.session.mode() !== 'full' || this.session.status() !== 'authenticated') return;
    const generation = this.session.generation();
    this.localeToken += 1;
    const token = this.localeToken;
    this.localeSavingState.set(true);
    try {
      const summary = await this.meRepository.updateUiLocale(locale);
      if (this.isCurrent(generation, token, this.localeToken)) {
        this.meToken += 1; // the PATCH result is newer than any GET still in flight: that GET must be discarded
        this.session.setMe(summary);
      }
    } catch (cause) {
      if (!this.isCurrent(generation, token, this.localeToken)) return;
      if (this.toAppError(cause).status === 401) return;
      const message = await firstValueFrom(this.transloco.selectTranslate<string>('shell.language.saveFailed'));
      if (this.isCurrent(generation, token, this.localeToken)) {
        this.snack.open(message, undefined, { duration: SNACKBAR_DURATION_MS });
      }
    } finally {
      if (token === this.localeToken) this.localeSavingState.set(false);
    }
  }

  /**
   * `SessionStore.logout()` ends the session locally first; a server-side failure leaves nothing to restore.
   *
   * A dirty page is asked first (10.5.9-T1, README D18): the registry shares the page's in-flight confirm prompt, and
   * a "Stay" answer aborts the logout. Clean sessions keep the previous synchronous path. A terminal `SessionEnded`
   * never comes through here (`SessionStore` calls `signOut()` itself), so it is never deferred by this prompt.
   */
  logOut(): void {
    if (this.loggingOut) return;
    this.loggingOut = true;
    if (!this.unsavedWork.hasUnsaved()) { void this.finishLogout(); return; }
    void this.unsavedWork.confirmLeave().then(async (leave) => {
      if (leave) await this.finishLogout();
      else this.loggingOut = false;
    });
  }

  private async finishLogout(): Promise<void> {
    try {
      await this.session.logout();
    } catch {
      // The session is already gone locally (SessionStore.signOut); a server failure has nothing left to restore.
    } finally {
      this.loggingOut = false;
    }
  }

  resumeRealtime(): void {
    this.realtime.resume();
  }

  private async fetchMe(generation: number, token: number): Promise<void> {
    try {
      const summary = await this.meRepository.get();
      if (this.isCurrent(generation, token, this.meToken)) this.session.setMe(summary);
    } catch (cause) {
      if (!this.isCurrent(generation, token, this.meToken)) return;
      const error = this.toAppError(cause);
      if (error.status !== 401) this.profileErrorState.set(error);
    }
  }

  private isCurrent(generation: number, token: number, latest: number): boolean {
    return !this.destroyed && token === latest && this.session.generation() === generation;
  }

  private toAppError(cause: unknown): AppError {
    return isAppError(cause) ? cause : this.mapper.from(cause);
  }

  private async applyTitle(): Promise<void> {
    this.titleToken += 1;
    const token = this.titleToken;
    const key = deepestTitleKey(this.router.routerState.snapshot.root);
    if (key === null) {
      this.pageTitleState.set('');
      this.title.setTitle(productName ?? '');
      return;
    }
    const text = await firstValueFrom(this.transloco.selectTranslate<string>(key));
    if (this.destroyed || token !== this.titleToken) return;
    this.pageTitleState.set(text);
    this.title.setTitle(text);
  }
}
