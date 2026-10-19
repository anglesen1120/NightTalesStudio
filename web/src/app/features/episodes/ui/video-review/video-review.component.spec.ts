import { DeferBlockBehavior, DeferBlockState, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { LocaleService } from '@core/i18n/locale.service';
import { RetryCountdown } from '@shared/ui/retry-countdown/retry-countdown.component';
import { onEmit } from '@shared/ui/testing/on-emit';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import coreEn from '../../../../../assets/i18n/core/en.json';
import coreVi from '../../../../../assets/i18n/core/vi.json';
import episodesEn from '../../../../../assets/i18n/episodes/en.json';
import episodesVi from '../../../../../assets/i18n/episodes/vi.json';
import errorsEn from '../../../../../assets/i18n/errors/en.json';
import errorsVi from '../../../../../assets/i18n/errors/vi.json';
import { VideoReviewComponent } from './video-review.component';

async function setup(lang: 'vi' | 'en' = 'en') {
  vi.spyOn(HTMLMediaElement.prototype, 'load').mockImplementation(() => undefined);
  await TestBed.configureTestingModule({
    imports: [VideoReviewComponent, TranslocoTestingModule.forRoot({ langs: { en: { ...coreEn, episodes: episodesEn, errors: errorsEn }, vi: { ...coreVi, episodes: episodesVi, errors: errorsVi }, 'episodes/en': episodesEn, 'episodes/vi': episodesVi, 'errors/en': errorsEn, 'errors/vi': errorsVi }, translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: lang }, preloadLangs: true })],
    providers: [...ZONELESS_TEST_PROVIDERS, provideRouter([])],
    deferBlockBehavior: DeferBlockBehavior.Manual,
  }).compileComponents();
  TestBed.inject(LocaleService).activate(lang, { persist: false });
  const fixture = TestBed.createComponent(VideoReviewComponent);
  fixture.componentRef.setInput('episodeId', 'episode-1');
  fixture.componentRef.setInput('url', { url: 'https://media.example/video.mp4?signature=private', expiresAt: '2026-10-19T12:15:00Z' });
  for (const gate of ['canApprove', 'canReject', 'canDownload']) fixture.componentRef.setInput(gate, true);
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement };
}

afterEach(() => { TestBed.resetTestingModule(); vi.restoreAllMocks(); });

describe('VideoReviewComponent', () => {
  it.each(['vi', 'en'] as const)('keeps all decisions enabled with every advisory hint and reuses the skipped banner in %s', async (lang) => {
    const { fixture, root } = await setup(lang);
    fixture.componentRef.setInput('notes', ['subtitle_sync_suspect', 'subtitle_cues_long', 'music_none', 'scenes_trimmed', 'scenes_sparse', 'v9']);
    fixture.componentRef.setInput('skipped', [{ segment: 'p:3', reason: 'moderated' }, { segment: 'meta', reason: 'timeout' }]);
    await fixture.whenStable();
    const copy = (lang === 'vi' ? episodesVi : episodesEn).render;
    expect(root.querySelectorAll('[data-testid="quality-notes"] li')).toHaveLength(5);
    expect(root.textContent).toContain(copy.review.advisory);
    expect(root.textContent).not.toContain('v9');
    for (const id of ['approve-video', 'reject-video', 're-make-video']) {
      const button = root.querySelector<HTMLButtonElement>(`[data-testid="${id}"]`)!;
      expect(button.disabled).toBe(false);
      expect(button.hasAttribute('aria-disabled')).toBe(false);
    }
    expect(root.querySelector('[data-testid="skipped-scenes"] h3')?.textContent).toBe(copy.images.skippedBanner);
    expect(root.querySelectorAll('[data-testid="skipped-scenes"] li')).toHaveLength(2);
    expect(root.querySelector('a[href="/episodes/episode-1?segment=p:3"]')).not.toBeNull();
    expect(root.textContent).toContain(copy.images.moderatedScene);
    expect(root.textContent).toContain(copy.images.reason.timeout);
    expect(root.querySelector('nts-loading-state')).not.toBeNull();
    expect(root.querySelector('video')).toBeNull();
    const [block] = await fixture.getDeferBlocks();
    await block!.render(DeferBlockState.Complete);
    await fixture.whenStable();
    // C9: one defer for the pair — the container's — and the media element comes with the player, not one viewport later.
    expect(await block!.getDeferBlocks()).toHaveLength(0);
    expect(root.querySelector('video')).not.toBeNull();
    expect(root.textContent).not.toContain('signature=');
  });

  it('forwards each enabled decision intent exactly once', async () => {
    const { fixture, root } = await setup();
    const counts: [number, number, number] = [0, 0, 0];
    onEmit(fixture.componentInstance.approveRequest, () => counts[0]++);
    onEmit(fixture.componentInstance.rejectRequest, () => counts[1]++);
    onEmit(fixture.componentInstance.reMakeRequest, () => counts[2]++);
    for (const id of ['approve-video', 'reject-video', 're-make-video']) root.querySelector<HTMLButtonElement>(`[data-testid="${id}"]`)!.click();
    expect(counts).toEqual([1, 1, 1]);
  });

  it.each(['not_found', 'network_error', 'service_unavailable'] as const)('shows mapped %s failures with no stale player and a manual retry', async (code) => {
    const { fixture, root } = await setup();
    fixture.componentRef.setInput('url', null);
    fixture.componentRef.setInput('urlFailed', true);
    fixture.componentRef.setInput('urlErrorKey', `errors.${code}`);
    await fixture.whenStable();
    expect(root.querySelector('nts-error-state')).not.toBeNull();
    expect(root.textContent).toContain(errorsEn[code]);
    expect(root.querySelector('nts-video-player, video')).toBeNull();
    let retries = 0;
    onEmit(fixture.componentInstance.retryUrl, () => retries++);
    root.querySelector<HTMLButtonElement>('.url-error button')!.click();
    expect(retries).toBe(1);
  });

  it('holds the storage retry until RetryCountdown ends and disables download with the same message', async () => {
    const { fixture, root } = await setup();
    fixture.componentRef.setInput('urlErrorKey', 'errors.service_unavailable');
    fixture.componentRef.setInput('urlRetryAfterSec', 5);
    await fixture.whenStable();
    expect(root.querySelector('a[data-testid="download-mp4"]')).toBeNull();
    expect(root.querySelector('.download-disabled')?.textContent).toContain(errorsEn.service_unavailable);
    const retry = root.querySelector<HTMLButtonElement>('.url-error button')!;
    let retries = 0;
    onEmit(fixture.componentInstance.retryUrl, () => retries++);
    // C10: the wait is reported without the native attribute, so the button the user just clicked keeps their focus.
    expect(retry.disabled).toBe(false);
    expect(retry.getAttribute('aria-disabled')).toBe('true');
    expect(retry.getAttribute('aria-busy')).toBe('true');
    retry.click();
    expect(retries).toBe(0);
    const countdown = fixture.debugElement.query(By.directive(RetryCountdown)).componentInstance as RetryCountdown;
    countdown.remaining.set(0);
    await fixture.whenStable();
    expect(retry.getAttribute('aria-disabled')).toBeNull();
    expect(retry.getAttribute('aria-busy')).toBeNull();
    retry.click();
    expect(retries).toBe(1);
  });

  it('marks the retry busy without the native attribute while a read is in flight, and ignores that click', async () => {
    const { fixture, root } = await setup();
    fixture.componentRef.setInput('url', null);
    fixture.componentRef.setInput('urlFailed', false);
    fixture.componentRef.setInput('urlErrorKey', 'errors.not_found');
    fixture.componentRef.setInput('loadingUrl', true);
    await fixture.whenStable();
    const retry = root.querySelector<HTMLButtonElement>('.url-error button')!;
    let retries = 0;
    onEmit(fixture.componentInstance.retryUrl, () => retries++);
    expect(retry.disabled).toBe(false);
    expect(retry.getAttribute('aria-disabled')).toBe('true');
    expect(retry.getAttribute('aria-busy')).toBe('true');
    retry.click();
    expect(retries).toBe(0);
    fixture.componentRef.setInput('loadingUrl', false);
    await fixture.whenStable();
    expect(retry.getAttribute('aria-disabled')).toBeNull();
    retry.click();
    expect(retries).toBe(1);
  });

  it('renders ready as metadata and download only, with no player, hints, scenes or decisions', async () => {
    const { fixture, root } = await setup();
    fixture.componentRef.setInput('ready', true);
    fixture.componentRef.setInput('finalVideo', { assetId: 'asset-1', sizeBytes: 1_000_000, durationSec: 60, retentionUntil: '2026-11-19T12:00:00Z', sha256: 'a'.repeat(64), contentType: 'video/mp4' });
    await fixture.whenStable();
    expect(root.querySelector('dl')).not.toBeNull();
    expect(root.querySelector('a[data-testid="download-mp4"]')).not.toBeNull();
    expect(root.querySelector('nts-video-player, nts-quality-notes, nts-skipped-scenes, button')).toBeNull();
  });

  it('handles initial loading, empty metadata and no skipped images without inventing a banner', async () => {
    const { fixture, root } = await setup();
    fixture.componentRef.setInput('url', null);
    fixture.componentRef.setInput('loadingUrl', true);
    await fixture.whenStable();
    expect(root.querySelector('nts-loading-state')).not.toBeNull();
    expect(root.querySelector('nts-video-player, dl, nts-skipped-scenes')).toBeNull();
    expect(root.textContent).toContain(episodesEn.render.review['hints.none']);
  });

  it('contains no credit or price copy in either complete locale catalogue', () => {
    const values = (value: unknown): string[] => typeof value === 'string' ? [value] : Object.values(value as Record<string, unknown>).flatMap(values);
    for (const copy of [episodesEn, episodesVi]) {
      expect(values(copy.render.review).join(' ')).not.toMatch(/\b(credits?|cost|price|balance)\b|tín dụng|chi phí|số dư/i);
      expect(Object.keys(copy.render.review).sort()).toEqual(Object.keys(episodesEn.render.review).sort());
      expect(Object.keys(copy.render.quality).sort()).toEqual(Object.keys(episodesEn.render.quality).sort());
    }
  });
});
