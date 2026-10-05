import { LiveAnnouncer } from '@angular/cdk/a11y';
import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { of, throwError } from 'rxjs';
import type { AppError } from '@core/errors';
import { SessionApi } from '@core/session';
import { FakeSessionApi } from '../../../core/session/testing/fake-session.api';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { FakeCatalogRepository } from '../testing/fake-catalog.repository';
import { CatalogRepository } from './catalog.repository';
import { VoiceStepStore } from './voice-step.store';

class FakeAnnouncer {
  readonly messages: string[] = [];
  announce(message: string): Promise<void> {
    this.messages.push(message);
    return Promise.resolve();
  }
}

function appError(code: string, status: number, kind: AppError['kind'], extra: Partial<AppError> = {}): AppError {
  return { code, status, kind, fieldErrors: {}, ...extra };
}

interface SetupOptions {
  /**
   * Replaces the injected `TranslocoService`, e.g. with a failing `selectTranslate` (PS-4 regression).
   * `load` must be present: `TranslocoTestingModule`'s `preloadLangs` initializer calls it once per
   * language in `langs` before the test module finishes compiling.
   */
  readonly transloco?: { selectTranslate: (key: string) => unknown; load: (lang: string) => unknown };
}

function setup(options: SetupOptions = {}): { store: VoiceStepStore; repo: FakeCatalogRepository; announcer: FakeAnnouncer } {
  const repo = new FakeCatalogRepository();
  const announcer = new FakeAnnouncer();
  TestBed.configureTestingModule({
    imports: [
      TranslocoTestingModule.forRoot({
        langs: { vi: { series: { voice: { clearedByLanguage: 'Đã xoá giọng đọc' } } } },
        translocoConfig: { availableLangs: ['vi'], defaultLang: 'vi' },
        preloadLangs: true,
      }),
    ],
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      { provide: SessionApi, useValue: new FakeSessionApi() },
      { provide: CatalogRepository, useValue: repo },
      { provide: LiveAnnouncer, useValue: announcer },
      ...(options.transloco === undefined ? [] : [{ provide: TranslocoService, useValue: options.transloco }]),
      VoiceStepStore,
    ],
  });
  return { store: TestBed.inject(VoiceStepStore), repo, announcer };
}

describe('VoiceStepStore', () => {
  it('VoiceStepStore_Load_StoresVoices_And_ExposesSelected', async () => {
    const { store } = setup();
    await store.load('vi');
    expect(store.voices().map((v) => v.id)).toEqual(['vi-quiet', 'vi-warm', 'vi-hue']);
    expect(store.loading()).toBe(false);
    expect(store.error()).toBeNull();
    store.select('vi-warm');
    expect(store.selectedId()).toBe('vi-warm');
    expect(store.selected()?.displayName).toBe('Lan');
    expect(store.compatible()).toBe(true);
  });

  it('VoiceStepStore_Load_IsSingleFlightPerLanguage', async () => {
    const { store, repo } = setup();
    repo.defer();
    const first = store.load('vi');
    const second = store.load('vi');
    expect(first).toBe(second);
    repo.resolveNext();
    await Promise.all([first, second]);
    expect(repo.voicedCalls).toEqual(['vi']);
  });

  it('VoiceStepStore_Load_AfterCompletion_IssuesANewRequest', async () => {
    const { store, repo } = setup();
    await store.load('vi');
    await store.load('vi');
    expect(repo.voicedCalls).toEqual(['vi', 'vi']);
  });

  it('VoiceStepStore_Load_NewLanguage_ClearsSelection', async () => {
    const { store } = setup();
    await store.load('vi');
    store.select('vi-warm');
    const next = store.load('en');
    expect(store.selectedId()).toBeNull();
    await next;
    expect(store.voices().map((v) => v.id)).toEqual(['en-news']);
    expect(store.compatible()).toBe(true);
  });

  it('VoiceStep_LanguageChange_ClearsVoice_AndAnnounces', async () => {
    const { store, announcer } = setup();
    await store.load('vi');
    store.select('vi-warm');
    await store.load('en');
    await vi.waitFor(() => expect(announcer.messages).toEqual(['Đã xoá giọng đọc']));
  });

  it('VoiceStep_LanguageChange_AnnounceTranslationFailure_DoesNotRejectTheLoad', async () => {
    // PS-4: the cleared-by-language announcement is fire-and-forget; a failing translation lookup must be
    // swallowed, never surface as an unhandled rejection (which vitest would fail this file for).
    const unhandled: unknown[] = [];
    const onGlobalUnhandled = (event: PromiseRejectionEvent): void => {
      event.preventDefault();
      unhandled.push(event.reason);
    };
    const onProcessUnhandled = (reason: unknown): void => {
      unhandled.push(reason);
    };
    globalThis.addEventListener('unhandledrejection', onGlobalUnhandled);
    process.on('unhandledRejection', onProcessUnhandled);
    try {
      const { store, announcer } = setup({
        transloco: {
          load: () => of({}),
          selectTranslate: () => throwError(() => new Error('translations unavailable')),
        },
      });
      await store.load('vi');
      store.select('vi-warm');

      await expect(store.load('en')).resolves.toBeUndefined();

      // The tsconfig lib predates Promise.withResolvers, so the executor form is the only typed option here.
      await new Promise<void>((resolve) => {
        setTimeout(resolve, 20);
      });
      expect(unhandled).toEqual([]);
      expect(store.selectedId()).toBeNull();
      expect(store.voices().map((v) => v.id)).toEqual(['en-news']);
      expect(store.error()).toBeNull();
      expect(announcer.messages).toEqual([]);
    } finally {
      globalThis.removeEventListener('unhandledrejection', onGlobalUnhandled);
      process.off('unhandledRejection', onProcessUnhandled);
    }
  });

  it('VoiceStepStore_Load_NewLanguageWithoutSelection_DoesNotAnnounce', async () => {
    const { store, announcer } = setup();
    await store.load('vi');
    await store.load('en');
    await Promise.resolve();
    expect(announcer.messages).toEqual([]);
  });

  it('VoiceStepStore_Load_KeepsPreviousListWhileNewLanguageLoads', async () => {
    const { store, repo } = setup();
    await store.load('vi');
    repo.defer();
    const next = store.load('en');
    expect(store.loading()).toBe(true);
    expect(store.voices().length).toBe(3);
    repo.resolveNext();
    await next;
    expect(store.loading()).toBe(false);
  });

  it('VoiceStepStore_Select_IgnoresUnknownId', async () => {
    const { store } = setup();
    await store.load('vi');
    store.select('nope');
    expect(store.selectedId()).toBeNull();
  });

  it('VoiceStepStore_Select_IgnoresVoiceOfAnotherLanguage', async () => {
    const { store, repo } = setup();
    await store.load('vi');
    repo.defer();
    const next = store.load('en');
    store.select('vi-warm');
    expect(store.selectedId()).toBeNull();
    repo.resolveNext();
    await next;
  });

  it('VoiceStepStore_Load_ValidationFailed_KeepsLastGoodList_AndSetsFieldError', async () => {
    const { store, repo } = setup();
    await store.load('vi');
    repo.failWith(appError('validation_failed', 400, 'validation'));
    await store.load('vi');
    expect(store.voices().length).toBe(3);
    expect(store.fieldError()).toBe('errors.validation_failed');
    expect(store.error()).toBeNull();
    expect(store.loading()).toBe(false);
  });

  it('VoiceStepStore_Load_NetworkError_SetsError_AndKeepsList', async () => {
    const { store, repo } = setup();
    await store.load('vi');
    const error = appError('network_error', 0, 'network');
    repo.failWith(error);
    await store.load('vi');
    expect(store.error()).toBe(error);
    expect(store.voices().length).toBe(3);
    expect(store.retryAfterSec()).toBeNull();
  });

  it('VoiceStepStore_Load_RateLimited_SetsRetryAfterSec', async () => {
    const { store, repo } = setup();
    repo.failWith(appError('rate_limited', 429, 'rate_limit', { retryAfterSec: 30 }));
    await store.load('vi');
    expect(store.error()?.code).toBe('rate_limited');
    expect(store.retryAfterSec()).toBe(30);
  });

  it('VoiceStepStore_Retry_ReloadsCurrentLanguage_AndClearsError', async () => {
    const { store, repo } = setup();
    repo.failWith(appError('network_error', 0, 'network'));
    await store.load('vi');
    repo.failWith(null);
    await store.retry();
    expect(store.error()).toBeNull();
    expect(store.voices().length).toBe(3);
    expect(repo.voicedCalls).toEqual(['vi', 'vi']);
  });

  it('VoiceStepStore_Load_LateLanguageResponse_IsDiscarded', async () => {
    const { store, repo } = setup();
    repo.defer();
    const viLoad = store.load('vi');
    const enLoad = store.load('en');
    repo.resolveLanguage('en');
    await enLoad;
    repo.resolveLanguage('vi');
    await viLoad;
    expect(store.language()).toBe('en');
    expect(store.voices().map((v) => v.id)).toEqual(['en-news']);
    expect(store.loading()).toBe(false);
  });

  it('VoiceStepStore_Load_SwitchBackWhileOldRequestPending_StartsAFreshRequest', async () => {
    const { store, repo } = setup();
    repo.defer();
    const first = store.load('vi');
    void store.load('en');
    const again = store.load('vi');
    expect(again).not.toBe(first);
    expect(repo.voicedCalls).toEqual(['vi', 'en', 'vi']);
    repo.resolveNext();
    repo.resolveNext();
    repo.resolveNext();
    await again;
    expect(store.voices().map((v) => v.id)).toEqual(['vi-quiet', 'vi-warm', 'vi-hue']);
    expect(store.loading()).toBe(false);
  });

  it('VoiceStepStore_Load_NotModified_KeepsList', async () => {
    const { store, repo } = setup();
    repo.willServe(repo['catalog'], '"v1"');
    await store.load('vi');
    const list = store.voices();
    repo.answerNotModified();
    const reload = store.load('vi');
    expect(store.loading()).toBe(true);
    await reload;
    expect(repo.ifNoneMatchCalls).toEqual([null, '"v1"']);
    expect(store.voices()).toBe(list);
    expect(store.error()).toBeNull();
    expect(store.loading()).toBe(false);
  });
});
