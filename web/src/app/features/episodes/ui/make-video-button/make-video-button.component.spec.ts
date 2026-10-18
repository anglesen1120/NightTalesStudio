import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { onEmit } from '@shared/ui/testing/on-emit';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import episodesEn from '../../../../../assets/i18n/episodes/en.json';
import episodesVi from '../../../../../assets/i18n/episodes/vi.json';
import { MakeVideoButtonComponent } from './make-video-button.component';

async function setup(lang: 'vi' | 'en' = 'en') {
  await TestBed.configureTestingModule({
    imports: [MakeVideoButtonComponent, TranslocoTestingModule.forRoot({
      langs: { en: { episodes: episodesEn }, vi: { episodes: episodesVi }, 'episodes/en': episodesEn, 'episodes/vi': episodesVi },
      translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: lang, fallbackLang: lang, reRenderOnLangChange: true },
      preloadLangs: true,
    })],
    providers: [...ZONELESS_TEST_PROVIDERS],
  }).compileComponents();
  const fixture = TestBed.createComponent(MakeVideoButtonComponent);
  fixture.componentRef.setInput('labelKey', 'episodes.render.make.action');
  await fixture.whenStable();
  const root = fixture.nativeElement as HTMLElement;
  return { fixture, root, button: root.querySelector<HTMLButtonElement>('[data-testid="make-video"]')! };
}

afterEach(() => TestBed.resetTestingModule());

describe('MakeVideoButtonComponent', () => {
  it.each(['vi', 'en'] as const)('renders enabled action and retry labels from full keys in %s', async (lang) => {
    const { fixture, button } = await setup(lang);
    const messages = lang === 'vi' ? episodesVi : episodesEn;
    expect(button.type).toBe('button');
    expect(button.hasAttribute('aria-disabled')).toBe(false);
    expect(button.textContent?.trim()).toBe(messages.render.make.action);
    expect(button.hasAttribute('aria-busy')).toBe(false);
    fixture.componentRef.setInput('labelKey', 'episodes.render.make.retry');
    await fixture.whenStable();
    expect(button.textContent?.trim()).toBe(messages.render.make.retry);
  });

  it.each(['vi', 'en'] as const)('links the disabled button to a visible reason and removes it when enabled in %s', async (lang) => {
    const { fixture, root, button } = await setup(lang);
    fixture.componentRef.setInput('disabled', true);
    fixture.componentRef.setInput('reasonKey', 'episodes.render.make.blockedKeywords');
    await fixture.whenStable();
    const reasonId = button.getAttribute('aria-describedby');
    expect(reasonId).toBeTruthy();
    const reason = root.querySelector<HTMLElement>(`#${reasonId}`)!;
    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(reason.tagName).toBe('P');
    expect(reason.hidden).toBe(false);
    expect(reason.hasAttribute('aria-hidden')).toBe(false);
    expect(reason.textContent).toBe((lang === 'vi' ? episodesVi : episodesEn).render.make.blockedKeywords);
    fixture.componentRef.setInput('disabled', false);
    await fixture.whenStable();
    expect(button.hasAttribute('aria-describedby')).toBe(false);
    expect(root.querySelector(`#${reasonId}`)).toBeNull();
    expect(button.hasAttribute('aria-disabled')).toBe(false);
  });

  it('gives each rendered reason its own description id', async () => {
    const { fixture, button } = await setup();
    const other = TestBed.createComponent(MakeVideoButtonComponent);
    for (const component of [fixture, other]) {
      component.componentRef.setInput('labelKey', 'episodes.render.make.action');
      component.componentRef.setInput('disabled', true);
      component.componentRef.setInput('reasonKey', 'episodes.render.make.blockedKeywords');
    }
    await Promise.all([fixture.whenStable(), other.whenStable()]);
    const otherRoot = other.nativeElement as HTMLElement;
    const otherId = otherRoot.querySelector('button')!.getAttribute('aria-describedby');
    expect(otherId).not.toBe(button.getAttribute('aria-describedby'));
    expect(otherRoot.querySelector(`#${otherId}`)).not.toBeNull();
    other.destroy();
  });

  it.each(['vi', 'en'] as const)('shows a disabled busy action and restores the chosen label after submission in %s', async (lang) => {
    const { fixture, button } = await setup(lang);
    fixture.componentRef.setInput('labelKey', 'episodes.render.make.retry');
    fixture.componentRef.setInput('busy', true);
    await fixture.whenStable();
    const messages = lang === 'vi' ? episodesVi : episodesEn;
    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(button.hasAttribute('disabled')).toBe(false);
    button.focus();
    expect(document.activeElement).toBe(button);
    expect(button.getAttribute('aria-busy')).toBe('true');
    expect(button.textContent?.trim()).toBe(messages.render.make.inProgress);
    fixture.componentRef.setInput('busy', false);
    await fixture.whenStable();
    expect(button.hasAttribute('aria-disabled')).toBe(false);
    expect(button.hasAttribute('aria-busy')).toBe(false);
    expect(button.textContent?.trim()).toBe(messages.render.make.retry);
  });

  it('emits exactly one request for each enabled click', async () => {
    const { fixture, button } = await setup();
    let requests = 0;
    onEmit(fixture.componentInstance.request, () => requests++);
    button.click();
    expect(requests).toBe(1);
    button.click();
    expect(requests).toBe(2);
  });

  it.each(['disabled', 'busy'] as const)('emits no request while %s, including a synthetic click', async (inputName) => {
    const { fixture, button } = await setup();
    let requests = 0;
    onEmit(fixture.componentInstance.request, () => requests++);
    fixture.componentRef.setInput(inputName, true);
    await fixture.whenStable();
    button.click();
    const click = new MouseEvent('click', { bubbles: true, cancelable: true });
    button.dispatchEvent(click);
    expect(click.defaultPrevented).toBe(true);
    expect(requests).toBe(0);
  });

  it('does not leave a dangling description when disabled without a reason key', async () => {
    const { fixture, root, button } = await setup();
    fixture.componentRef.setInput('disabled', true);
    await fixture.whenStable();
    expect(button.hasAttribute('aria-describedby')).toBe(false);
    expect(root.querySelector('.reason')).toBeNull();
  });
});
