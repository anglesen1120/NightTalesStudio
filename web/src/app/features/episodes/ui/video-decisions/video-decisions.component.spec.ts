import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { onEmit } from '@shared/ui/testing/on-emit';
import { safeFilename } from '@shared/util/safe-filename';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import episodesEn from '../../../../../assets/i18n/episodes/en.json';
import episodesVi from '../../../../../assets/i18n/episodes/vi.json';
import { VideoDecisionsComponent } from './video-decisions.component';

async function setup(lang: 'vi' | 'en' = 'en') {
  await TestBed.configureTestingModule({ imports: [VideoDecisionsComponent, TranslocoTestingModule.forRoot({ langs: { en: { episodes: episodesEn }, vi: { episodes: episodesVi } }, translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: lang }, preloadLangs: true })], providers: ZONELESS_TEST_PROVIDERS }).compileComponents();
  const fixture = TestBed.createComponent(VideoDecisionsComponent);
  for (const key of ['canApprove', 'canReject', 'canDownload']) fixture.componentRef.setInput(key, true);
  fixture.componentRef.setInput('url', 'https://media.example/video.mp4?signature=secret');
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement };
}

afterEach(() => TestBed.resetTestingModule());

describe('VideoDecisionsComponent', () => {
  it.each(['vi', 'en'] as const)('names actions and emits each enabled intent exactly once in %s', async (lang) => {
    const { fixture, root } = await setup(lang);
    const counts: [number, number, number] = [0, 0, 0];
    onEmit(fixture.componentInstance.approveRequest, () => counts[0]++);
    onEmit(fixture.componentInstance.rejectRequest, () => counts[1]++);
    onEmit(fixture.componentInstance.reMakeRequest, () => counts[2]++);
    for (const button of root.querySelectorAll<HTMLButtonElement>('button')) button.click();
    expect(counts).toEqual([1, 1, 1]);
    const copy = (lang === 'vi' ? episodesVi : episodesEn).render.review;
    expect([...root.querySelectorAll('button')].map((button) => button.textContent?.trim())).toEqual([copy.approve, copy.reject, copy.reMake]);
    expect(root.querySelector('[cost-line]')).toBeNull();
    expect(root.textContent).not.toMatch(/\b(credits?|cost|price|balance)\b|tín dụng|chi phí|số dư/i);
  });

  it.each(['canApprove', 'canReject', 'busy'] as const)('guards synthetic clicks using %s while keeping triggers focusable', async (gate) => {
    const { fixture, root } = await setup();
    const counts: [number, number, number] = [0, 0, 0];
    onEmit(fixture.componentInstance.approveRequest, () => counts[0]++);
    onEmit(fixture.componentInstance.rejectRequest, () => counts[1]++);
    onEmit(fixture.componentInstance.reMakeRequest, () => counts[2]++);
    fixture.componentRef.setInput(gate, gate === 'busy');
    await fixture.whenStable();
    for (const button of root.querySelectorAll<HTMLButtonElement>('button')) { expect(button.disabled).toBe(false); button.click(); }
    expect(counts).toEqual(gate === 'busy' ? [0, 0, 0] : gate === 'canApprove' ? [0, 1, 1] : [1, 0, 0]);
    if (gate === 'busy') expect(root.querySelectorAll('[aria-busy="true"]')).toHaveLength(3);
  });

  it.each(['a"/\\\u0001 title', 'x'.repeat(200)])('preserves the safe suggested filename and current signed download URL for hostile titles', async (title) => {
    const { fixture, root } = await setup();
    const filename = `${safeFilename(title)}.mp4`;
    fixture.componentRef.setInput('downloadName', filename);
    await fixture.whenStable();
    const link = root.querySelector<HTMLAnchorElement>('[data-testid="download-mp4"]')!;
    expect(link.getAttribute('href')).toBe('https://media.example/video.mp4?signature=secret');
    expect(link.getAttribute('download')).toBe(filename);
    expect(link.rel).toBe('noopener');
    expect(link.target).toBe('_blank');
    expect(root.textContent).not.toContain('signature=');
    fixture.componentRef.setInput('canDownload', false);
    await fixture.whenStable();
    expect(root.querySelector('a, .download-disabled')).toBeNull();
  });

  it('replaces a degraded download with a disabled message, never a dead href', async () => {
    const { fixture, root } = await setup();
    fixture.componentRef.setInput('downloadErrorKey', 'episodes.render.review.urlFailed');
    await fixture.whenStable();
    expect(root.querySelector('a')).toBeNull();
    expect(root.querySelector('.download-disabled')?.getAttribute('aria-disabled')).toBe('true');
    expect(root.querySelector('.download-disabled')?.textContent).toContain(episodesEn.render.review.urlFailed);
  });
});
