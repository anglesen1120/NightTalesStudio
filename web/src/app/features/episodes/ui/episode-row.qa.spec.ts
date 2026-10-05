import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import { episodeItem } from '../testing/fake-episodes.repository';
import { EpisodeRowComponent } from './episode-row.component';

// QA (10.5.1-T1): titles come straight from the API and are the one place user text reaches the list DOM. Prove the
// interpolation escapes it, and probe the "no usable title" boundary the shipped spec only covers for `null`.

const translations = TranslocoTestingModule.forRoot({
  langs: { 'episodes/vi': episodesVi },
  translocoConfig: {
    availableLangs: ['vi'],
    defaultLang: 'vi',
    fallbackLang: 'vi',
    reRenderOnLangChange: true,
  },
  preloadLangs: true,
});

@Component({ template: '', changeDetection: ChangeDetectionStrategy.OnPush })
class EpisodeDetailStub {}

async function setup() {
  await TestBed.configureTestingModule({
    imports: [EpisodeRowComponent, translations],
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([{ path: 'episodes/:id', component: EpisodeDetailStub }]),
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(EpisodeRowComponent);
  return { fixture, root: fixture.nativeElement as HTMLElement };
}

afterEach(() => TestBed.resetTestingModule());

describe('EpisodeRowComponent (QA)', () => {
  it('renders a title containing markup as inert text, never as elements', async () => {
    const { fixture, root } = await setup();
    const payload = '<img src=x onerror="window.__ntsQaXss=1"><script>window.__ntsQaXss=1</script>';
    fixture.componentRef.setInput('item', episodeItem(1, { title: payload }));
    fixture.detectChanges();
    await fixture.whenStable();

    const title = root.querySelector('.episode-row__title');
    expect(title?.querySelector('img, script, b')).toBeNull();
    expect(title?.textContent).toContain(payload);
  });

  it('gives the title link a non-empty accessible name even for an empty-string title', async () => {
    const { fixture, root } = await setup();
    fixture.componentRef.setInput('item', episodeItem(1, { title: '' }));
    fixture.detectChanges();
    await fixture.whenStable();

    expect(root.querySelector('.episode-row__title')?.textContent?.trim()).toBe(episodesVi.list.untitled);
  });

  it.each([' ', '\t\n', '\u00a0\u2003'])('gives whitespace-only title %j a translated accessible name', async (title) => {
    const { fixture, root } = await setup();
    fixture.componentRef.setInput('item', episodeItem(1, { title }));
    fixture.detectChanges();
    await fixture.whenStable();

    expect(root.querySelector('.episode-row__title')?.textContent?.trim()).toBe(episodesVi.list.untitled);
  });
});
