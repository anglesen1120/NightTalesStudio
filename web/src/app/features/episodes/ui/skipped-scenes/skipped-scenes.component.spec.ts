import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import episodesEn from '../../../../../assets/i18n/episodes/en.json';
import episodesVi from '../../../../../assets/i18n/episodes/vi.json';
import type { SkippedScene } from '../../domain/render.model';
import { SkippedScenesComponent } from './skipped-scenes.component';

async function setup(skipped: readonly SkippedScene[], lang: 'vi' | 'en' = 'en') {
  await TestBed.configureTestingModule({
    imports: [SkippedScenesComponent, TranslocoTestingModule.forRoot({
      langs: { en: { episodes: episodesEn }, vi: { episodes: episodesVi }, 'episodes/en': episodesEn, 'episodes/vi': episodesVi },
      translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: lang, fallbackLang: lang },
      preloadLangs: true,
    })],
    providers: [...ZONELESS_TEST_PROVIDERS, provideRouter([])],
  }).compileComponents();
  const fixture = TestBed.createComponent(SkippedScenesComponent);
  fixture.componentRef.setInput('skipped', skipped);
  fixture.componentRef.setInput('episodeId', 'episode-1');
  fixture.componentRef.setInput('headingKey', 'render.failure.scenes');
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement };
}

afterEach(() => TestBed.resetTestingModule());

describe('SkippedScenesComponent', () => {
  it.each(['vi', 'en'] as const)('supports both consumer headings and moderated guidance in %s', async (lang) => {
    const { fixture, root } = await setup([{ segment: 'p:3', reason: 'moderated' }, { segment: 'hook', reason: 'timeout' }], lang);
    const messages = lang === 'vi' ? episodesVi : episodesEn;
    expect(root.querySelector('h3')?.textContent).toBe(messages.render.failure.scenes);
    expect(root.querySelectorAll('li')).toHaveLength(2);
    const link = root.querySelector<HTMLAnchorElement>('a')!;
    expect(link.textContent).toBe('p:3');
    expect(link.getAttribute('href')).toBe('/episodes/episode-1?segment=p:3');
    expect(root.querySelectorAll('.scene-guidance')).toHaveLength(1);
    expect(root.querySelector('.scene-guidance')?.textContent).toBe(messages.render.images.moderatedScene);
    expect(root.textContent).toContain(messages.render.images.reason.timeout);
    fixture.componentRef.setInput('headingKey', 'render.images.skippedBanner');
    await fixture.whenStable();
    expect(root.querySelector('h3')?.textContent).toBe(messages.render.images.skippedBanner);
    expect([...root.querySelectorAll('.scene-count')].map((node) => node.textContent)).toEqual(['1.', '2.']);
    expect(root.querySelector('nts-icon-alert')).not.toBeNull();
  });

  it('replaces invalid addresses with neutral scene labels and never renders an unknown reason', async () => {
    const poisonedAddress = '<img src=x onerror=1>';
    const javascriptAddress = 'javascript:alert(1)';
    const unsafeIntegerAddress = 'p:99999999999999999999';
    const { root } = await setup([
      { segment: poisonedAddress, reason: 'private-provider-detail' },
      { segment: javascriptAddress, reason: 'private-provider-detail' },
      { segment: unsafeIntegerAddress, reason: 'private-provider-detail' },
      { segment: 'p:03', reason: 'constructor' },
    ]);
    expect(root.querySelector('a, img')).toBeNull();
    expect(root.querySelector('[onerror], [onload], [onclick]')).toBeNull();
    expect(root.textContent).not.toContain(poisonedAddress);
    expect(root.textContent).not.toContain(javascriptAddress);
    expect(root.textContent).not.toContain(unsafeIntegerAddress);
    expect(root.textContent).not.toContain('p:03');
    expect([1, 2, 3, 4].map((index) => episodesEn.render.images.sceneUnnamed.replace('{{index}}', String(index))).every((label) => root.textContent?.includes(label))).toBe(true);
    expect([...root.querySelectorAll('.scene-reason')].map((node) => node.textContent)).toEqual(Array.from({ length: 4 }, () => episodesEn.render.images.reason.unknown));
    expect(root.textContent).not.toContain('private-provider-detail');
    expect(root.textContent).not.toContain('constructor');
  });

  it('links a canonical paragraph address at the first index', async () => {
    const { root } = await setup([{ segment: 'p:0', reason: 'moderated' }]);
    const link = root.querySelector<HTMLAnchorElement>('a')!;
    expect(link.textContent).toBe('p:0');
    expect(link.getAttribute('href')).toBe('/episodes/episode-1?segment=p:0');
  });

  it('keeps duplicate segment rows counted without duplicate tracking keys', async () => {
    const { root } = await setup([{ segment: 'p:3', reason: 'rate_limited' }, { segment: 'p:3', reason: 'relay_rejected' }, { segment: 'p:3', reason: 'upstream_error' }]);
    expect(root.querySelectorAll('li')).toHaveLength(3);
    expect([...root.querySelectorAll('.scene-count')].map((node) => node.textContent)).toEqual(['1.', '2.', '3.']);
    expect(new Set([...root.querySelectorAll('.scene-reason')].map((node) => node.textContent)).size).toBe(3);
  });

  it('handles an empty list without inventing a scene', async () => {
    const { root } = await setup([]);
    expect(root.querySelector('li')).toBeNull();
  });
});
