import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting, type TestRequest } from '@angular/common/http/testing';
import { ChangeDetectionStrategy, Component, PLATFORM_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { TranslocoPipe, TranslocoService, provideTranslocoScope } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import coreEn from '../../../assets/i18n/core/en.json';
import coreVi from '../../../assets/i18n/core/vi.json';
import qualityVi from '../../../assets/i18n/quality/vi.json';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { LocaleService } from './locale.service';
import { provideAppI18n } from './transloco.config';
import { TranslocoHttpLoader } from './transloco.loader';

function setup(extra: readonly unknown[] = []): HttpTestingController {
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideHttpClient(),
      provideHttpClientTesting(),
      provideAppI18n(),
      ...(extra as []),
    ],
  });
  return TestBed.inject(HttpTestingController);
}

function urlsOf(requests: readonly TestRequest[]): string[] {
  return requests.map((request) => request.request.url).sort();
}

/** Captures `console.error` for one block; the loader logs one line per failed scope in development. */
async function captureConsoleError(run: () => Promise<void>): Promise<unknown[][]> {
  const calls: unknown[][] = [];
  const original = console.error;
  console.error = (...args: unknown[]) => {
    calls.push(args);
  };
  try {
    await run();
  } finally {
    console.error = original;
  }
  return calls;
}

@Component({
  imports: [TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<p id="close">{{ 'common.close' | transloco }}</p>`,
})
class CoreHost {}

@Component({
  imports: [TranslocoPipe],
  providers: [provideTranslocoScope('quality')],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<p id="hook">{{ 'quality.hook_missing' | transloco }}</p>`,
})
class QualityHost {}

describe('TranslocoHttpLoader', () => {
  it('requests a root-relative URL for the default (core) scope and for a named scope', async () => {
    const http = setup();
    const loader = TestBed.inject(TranslocoHttpLoader);

    const core = firstValueFrom(loader.getTranslation('vi'));
    http.expectOne('/assets/i18n/core/vi.json').flush(coreVi);
    expect(await core).toEqual(coreVi);

    const quality = firstValueFrom(loader.getTranslation('en', { scope: 'quality' }));
    http.expectOne('/assets/i18n/quality/en.json').flush({ hook_missing: 'x' });
    expect(await quality).toEqual({ hook_missing: 'x' });
    http.verify();
  });

  it('accepts the scoped path Transloco passes as lang ("quality/vi") and still requests one root-relative URL', async () => {
    const http = setup();
    const loader = TestBed.inject(TranslocoHttpLoader);
    const pending = firstValueFrom(loader.getTranslation('quality/vi', { scope: 'quality' }));
    http.expectOne('/assets/i18n/quality/vi.json').flush(qualityVi);
    expect(await pending).toEqual(qualityVi);
    http.verify();
  });

  it('issues one request per scope and language, however many callers ask', async () => {
    const http = setup();
    const loader = TestBed.inject(TranslocoHttpLoader);

    const first = firstValueFrom(loader.getTranslation('vi'));
    const second = firstValueFrom(loader.getTranslation('vi'));
    http.expectOne('/assets/i18n/core/vi.json').flush(coreVi);
    expect(await first).toBe(await second);

    await firstValueFrom(loader.getTranslation('vi'));
    http.expectNone('/assets/i18n/core/vi.json');
    const other = firstValueFrom(loader.getTranslation('en'));
    http.expectOne('/assets/i18n/core/en.json').flush(coreEn);
    await other;
    http.verify();
  });

  it('rejects on HTTP 404, logs once, and the next load retries and gets the real JSON', async () => {
    const http = setup();
    const loader = TestBed.inject(TranslocoHttpLoader);

    const logged = await captureConsoleError(async () => {
      const failed = firstValueFrom(loader.getTranslation('vi', { scope: 'quality' }));
      http.expectOne('/assets/i18n/quality/vi.json').flush('missing', { status: 404, statusText: 'Not Found' });
      await expect(failed).rejects.toBeTruthy();
    });
    expect(logged).toEqual([['i18n scope failed', 'quality', 'vi']]);

    const retried = firstValueFrom(loader.getTranslation('vi', { scope: 'quality' }));
    http.expectOne('/assets/i18n/quality/vi.json').flush(qualityVi);
    expect(await retried).toEqual(qualityVi);

    await firstValueFrom(loader.getTranslation('vi', { scope: 'quality' }));
    http.verify(); // the success is cached again: no third request
  });

  it('does not cache a network error either: failed then succeeded fetch yields the real JSON', async () => {
    const http = setup();
    const loader = TestBed.inject(TranslocoHttpLoader);
    await captureConsoleError(async () => {
      const failed = firstValueFrom(loader.getTranslation('en', { scope: 'quality' }));
      http.expectOne('/assets/i18n/quality/en.json').error(new ProgressEvent('error'));
      await expect(failed).rejects.toBeTruthy();
    });

    const retried = firstValueFrom(loader.getTranslation('en', { scope: 'quality' }));
    http.expectOne('/assets/i18n/quality/en.json').flush({ hook_missing: 'No hook' });
    expect(await retried).toEqual({ hook_missing: 'No hook' });
  });

  describe('input validation', () => {
    it.each(['../..', 'x/../y', '__proto__', 'Quality', 'a--b', '-a', 'a-', 'a/b', '', 'a.b', '%2e%2e'])(
      'resolves {} with no request for scope %j',
      async (scope) => {
        const http = setup();
        const loader = TestBed.inject(TranslocoHttpLoader);
        await captureConsoleError(async () => {
          expect(await firstValueFrom(loader.getTranslation('vi', { scope }))).toEqual({});
          expect(await firstValueFrom(loader.getTranslation(`${scope}/en`, { scope }))).toEqual({});
        });
        http.expectNone(() => true);
      },
    );

    it.each(['fr', 'VI', '__proto__', 'vi.json', '', 'x/__proto__', '../..'])('resolves {} with no request for language %j', async (lang) => {
      const http = setup();
      const loader = TestBed.inject(TranslocoHttpLoader);
      await captureConsoleError(async () => {
        expect(await firstValueFrom(loader.getTranslation(lang))).toEqual({});
        expect(await firstValueFrom(loader.getTranslation(lang, { scope: 'quality' }))).toEqual({});
      });
      http.expectNone(() => true);
    });

    it('rejects a hostile scope on the server platform before any bundle lookup', async () => {
      const http = setup([{ provide: PLATFORM_ID, useValue: 'server' }]);
      const loader = TestBed.inject(TranslocoHttpLoader);
      await captureConsoleError(async () => {
        expect(await firstValueFrom(loader.getTranslation('vi', { scope: '__proto__' }))).toEqual({});
      });
      http.expectNone(() => true);
    });
  });

  describe('on the prerender (server) platform', () => {
    it('uses the bundled translation and issues zero HTTP requests', async () => {
      const http = setup([{ provide: PLATFORM_ID, useValue: 'server' }]);
      const loader = TestBed.inject(TranslocoHttpLoader);

      const vi = (await firstValueFrom(loader.getTranslation('vi'))) as typeof coreVi;
      const en = (await firstValueFrom(loader.getTranslation('en'))) as typeof coreEn;

      expect(vi.common.close).toBe('Đóng');
      expect(en.common.close).toBe('Close');
      http.expectNone(() => true);
    });

    it('takes the error path (rejects + dev log, still no HTTP) for a scope that is not bundled, and does not cache it', async () => {
      const http = setup([{ provide: PLATFORM_ID, useValue: 'server' }]);
      const loader = TestBed.inject(TranslocoHttpLoader);
      const logged = await captureConsoleError(async () => {
        await expect(firstValueFrom(loader.getTranslation('vi', { scope: 'series' }))).rejects.toThrow('not bundled');
        await expect(firstValueFrom(loader.getTranslation('vi', { scope: 'series' }))).rejects.toThrow('not bundled');
      });
      expect(logged).toEqual([
        ['i18n scope failed', 'series', 'vi'],
        ['i18n scope failed', 'series', 'vi'],
      ]);
      http.expectNone(() => true);
    });
  });
});

describe('Transloco service with a failing scope', () => {
  const notFound = { status: 404, statusText: 'Not Found' };

  it('a scope that fails in vi and in the fallback language rejects once, and the next activation refetches the real JSON', async () => {
    const http = setup();
    const transloco = TestBed.inject(TranslocoService);
    const events: string[] = [];
    const first = await captureConsoleError(async () => {
      const core = firstValueFrom(transloco.load('vi'));
      http.expectOne('/assets/i18n/core/vi.json').flush(coreVi);
      await core;

      const failed = firstValueFrom(transloco.load('quality/vi')).then(
        () => events.push('resolved'),
        (error: unknown) => events.push(`rejected ${(error as Error).name}`),
      );
      const requests = http.match(() => true);
      events.push(`requests: ${urlsOf(requests).join(',')}`);
      for (const request of requests) request.flush('x', notFound);
      const fallbackRequests = http.match(() => true);
      events.push(`fallback requests: ${urlsOf(fallbackRequests).join(',')}`);
      for (const request of fallbackRequests) request.flush('x', notFound);
      await failed;
    });
    expect(first).toEqual(expect.arrayContaining([['i18n scope failed', 'quality', 'vi'], ['i18n scope failed', 'quality', 'en']]));
    expect(events).toEqual([
      'requests: /assets/i18n/quality/vi.json',
      'fallback requests: /assets/i18n/quality/en.json',
      'rejected TranslationLoadError',
    ]);

    const retry = firstValueFrom(transloco.load('quality/vi'));
    const again = http.match(() => true);
    expect(urlsOf(again)).toEqual(['/assets/i18n/quality/vi.json']);
    for (const request of again) request.flush(qualityVi);
    await retry;
    expect(transloco.translate('hook_missing', {}, 'quality/vi')).toBe(qualityVi.hook_missing);
  });

  it('a scope that fails in vi while its en file loads does not reject, stores nothing for vi, and retries on the next load', async () => {
    const http = setup();
    const transloco = TestBed.inject(TranslocoService);
    await captureConsoleError(async () => {
      const core = firstValueFrom(transloco.load('vi'));
      http.expectOne('/assets/i18n/core/vi.json').flush(coreVi);
      await core;

      const loading = firstValueFrom(transloco.load('quality/vi'));
      http.expectOne('/assets/i18n/quality/vi.json').flush('x', notFound);
      http.expectOne('/assets/i18n/quality/en.json').flush({ hook_missing: 'No hook' });
      await loading;
    });
    expect(transloco.translate('hook_missing', {}, 'quality/vi')).toBe('quality.hook_missing');

    const retry = firstValueFrom(transloco.load('quality/vi'));
    http.expectOne('/assets/i18n/quality/vi.json').flush(qualityVi);
    await retry;
    expect(transloco.translate('hook_missing', {}, 'quality/vi')).toBe(qualityVi.hook_missing);
  });
});

describe('Transloco scopes in templates', () => {
  it('loads core once per language and a lazy scope for the active language only', async () => {
    const http = setup();
    const locale = TestBed.inject(LocaleService);
    const fixture = TestBed.createComponent(QualityHost);
    fixture.detectChanges();

    expect(urlsOf(http.match(() => true).map((request) => {
      request.flush(request.request.url.includes('/quality/') ? qualityVi : coreVi);
      return request;
    }))).toEqual(['/assets/i18n/core/vi.json', '/assets/i18n/quality/vi.json']);
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).querySelector('#hook')?.textContent).toBe(qualityVi.hook_missing);

    locale.activate('en', { persist: false });
    const english = http.match(() => true);
    expect(urlsOf(english)).toEqual(['/assets/i18n/core/en.json', '/assets/i18n/quality/en.json']);
    for (const request of english) request.flush(request.request.url.includes('/quality/') ? { hook_missing: 'No hook' } : coreEn);
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).querySelector('#hook')?.textContent).toBe('No hook');

    locale.activate('vi', { persist: false });
    await fixture.whenStable();
    http.expectNone(() => true); // both vi files are cached: nothing is requested twice
    expect((fixture.nativeElement as HTMLElement).querySelector('#hook')?.textContent).toBe(qualityVi.hook_missing);
  });

  it('updates translated text in a zoneless host after whenStable, without a second detectChanges', async () => {
    const http = setup();
    const locale = TestBed.inject(LocaleService);
    const fixture = TestBed.createComponent(CoreHost);
    fixture.detectChanges();

    http.expectOne('/assets/i18n/core/vi.json').flush(coreVi);
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).querySelector('#close')?.textContent).toBe('Đóng');

    locale.activate('en', { persist: false });
    http.expectOne('/assets/i18n/core/en.json').flush(coreEn);
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).querySelector('#close')?.textContent).toBe('Close');
  });

  it('keeps the current choice when a slow scope load completes after vi→en→vi and after destroy', async () => {
    const http = setup();
    const locale = TestBed.inject(LocaleService);
    const transloco = TestBed.inject(TranslocoService);
    const fixture = TestBed.createComponent(QualityHost);
    fixture.detectChanges();
    const pending = http.match(() => true);
    expect(pending.length).toBeGreaterThan(0);

    locale.activate('en', { persist: false });
    locale.activate('vi', { persist: false });
    fixture.destroy();

    for (const request of pending) request.flush(request.request.url.includes('/quality/') ? qualityVi : coreVi);
    await Promise.resolve();

    expect(transloco.getActiveLang()).toBe('vi');
    expect(locale.locale()).toBe('vi');
    expect(document.documentElement.lang).toBe('vi');
    expect(locale.formatNumber(1234.5)).toBe('1.234,5');
  });

  it('a late response for the abandoned language never restores it', async () => {
    const http = setup();
    const locale = TestBed.inject(LocaleService);
    const transloco = TestBed.inject(TranslocoService);

    const slowVi = transloco.load('quality/vi');
    const viLoad = new Promise<void>((resolve) => slowVi.subscribe({ complete: resolve }));
    const viRequests = http.match(() => true);
    locale.activate('en', { persist: false });
    for (const request of viRequests) request.flush(request.request.url.includes('/quality/') ? qualityVi : coreVi);
    await viLoad;

    expect(transloco.getActiveLang()).toBe('en');
    expect(locale.locale()).toBe('en');
    expect(document.documentElement.lang).toBe('en');
  });
});
