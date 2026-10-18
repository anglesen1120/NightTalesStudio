import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { onEmit } from '@shared/ui/testing/on-emit';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import episodesEn from '../../../../../assets/i18n/episodes/en.json';
import episodesVi from '../../../../../assets/i18n/episodes/vi.json';
import errorsEn from '../../../../../assets/i18n/errors/en.json';
import errorsVi from '../../../../../assets/i18n/errors/vi.json';
import type { GuaranteedStage, RenderTimelineStep } from '../../domain/render.model';
import { RenderTimelineComponent } from './render-timeline.component';

function steps(active: GuaranteedStage): RenderTimelineStep[] {
  const keys: GuaranteedStage[] = ['queued', 'running', 'ingesting'];
  return keys.map((key, index) => ({ key, state: key === active ? 'active' : index < keys.indexOf(active) ? 'done' : 'todo', percent: null, subStepKey: null }));
}

function translated(key: string, params?: Record<string, string | number>): string {
  return TestBed.inject(TranslocoService).translate<string>(key, params);
}

async function setup(lang: 'vi' | 'en' = 'en') {
  await TestBed.configureTestingModule({
    imports: [RenderTimelineComponent, TranslocoTestingModule.forRoot({
      langs: { en: { episodes: episodesEn, errors: errorsEn }, vi: { episodes: episodesVi, errors: errorsVi }, 'episodes/en': episodesEn, 'episodes/vi': episodesVi, 'errors/en': errorsEn, 'errors/vi': errorsVi },
      translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: lang, fallbackLang: lang, reRenderOnLangChange: true },
      preloadLangs: true,
    })],
    providers: [...ZONELESS_TEST_PROVIDERS],
  }).compileComponents();
  const fixture = TestBed.createComponent(RenderTimelineComponent);
  fixture.componentRef.setInput('steps', steps('queued'));
  fixture.componentRef.setInput('percent', 0);
  fixture.componentRef.setInput('elapsedSec', 24);
  fixture.componentRef.setInput('showEmailNotice', true);
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement };
}

afterEach(() => TestBed.resetTestingModule());

describe('RenderTimelineComponent', () => {
  it.each(['vi', 'en'] as const)('renders ordered stages, named progress and departure guidance in %s', async (lang) => {
    const { root } = await setup(lang);
    expect([...root.querySelectorAll('[data-testid="render-stage"] strong')].map((node) => node.textContent?.trim())).toEqual(['queued', 'running', 'ingesting'].map((key) => translated(`episodes.render.stage.${key}`)));
    expect(root.querySelectorAll('[aria-current="step"]')).toHaveLength(1);
    expect(root.querySelector('[aria-current="step"] strong')?.textContent).toBe(translated('episodes.render.stage.queued'));
    const bar = root.querySelector('[role="progressbar"]')!;
    expect(bar.getAttribute('aria-label')).toBe(translated('episodes.render.timeline.heading'));
    expect(bar.getAttribute('aria-valuemin')).toBe('0');
    expect(bar.getAttribute('aria-valuemax')).toBe('100');
    expect(bar.getAttribute('aria-valuenow')).toBe('0');
    expect(bar.getAttribute('aria-valuetext')).toBe(`${translated('episodes.render.stage.queued')} — ${translated('episodes.render.timeline.percent', { percent: 0 })}`);
    expect(root.textContent).toContain(translated('episodes.render.timeline.elapsed', { duration: '0:24' }));
    expect(root.textContent).toContain(translated('episodes.render.timeline.emailNotice'));
    expect(root.textContent).toContain(translated('episodes.render.timeline.leave'));
  });

  it('announces stage/sub-stage changes, never percent or elapsed ticks', async () => {
    const { fixture, root } = await setup();
    const live = root.querySelector('[aria-live="polite"]')!;
    const summary = root.querySelector('[data-testid="render-elapsed-summary"]')!;
    expect(root.querySelectorAll('[aria-live]')).toHaveLength(1);
    expect(live.getAttribute('aria-atomic')).toBe('true');
    const queuedAnnouncement = live.textContent;
    const queuedDuration = summary.textContent;
    fixture.componentRef.setInput('steps', steps('queued'));
    fixture.componentRef.setInput('percent', 42);
    fixture.componentRef.setInput('elapsedSec', 31);
    await fixture.whenStable();
    expect(live.textContent).toBe(queuedAnnouncement);
    expect(summary.textContent).toBe(queuedDuration);
    expect(root.querySelector('.elapsed')?.getAttribute('aria-hidden')).toBe('true');
    expect(root.querySelector('.elapsed')?.hasAttribute('aria-live')).toBe(false);
    expect(root.querySelector('.elapsed')?.textContent).toContain('0:31');
    fixture.componentRef.setInput('steps', steps('running'));
    fixture.componentRef.setInput('subStepKey', 'assembling');
    await fixture.whenStable();
    expect(live.textContent).not.toBe(queuedAnnouncement);
    expect(live.textContent).toContain(translated('episodes.render.subStep.assembling'));
    expect(summary.textContent).toContain('0:31');
    expect(root.querySelector('[aria-current="step"] .sub-step')?.textContent).toContain(translated('episodes.render.subStep.assembling'));
    const assemblingAnnouncement = live.textContent;
    fixture.componentRef.setInput('percent', 55);
    fixture.componentRef.setInput('elapsedSec', 90);
    await fixture.whenStable();
    expect(live.textContent).toBe(assemblingAnnouncement);
    fixture.componentRef.setInput('subStepKey', 'checking');
    await fixture.whenStable();
    expect(live.textContent).not.toBe(assemblingAnnouncement);
    fixture.componentRef.setInput('steps', steps('ingesting'));
    await fixture.whenStable();
    expect(root.querySelector('.sub-step')).toBeNull();
    expect(root.querySelectorAll('[aria-current="step"]')).toHaveLength(1);
  });

  it('hides an absent sub-step and renders waiting as neutral text, not an error', async () => {
    const { fixture, root } = await setup();
    expect(root.querySelector('.sub-step')).toBeNull();
    expect(root.querySelector('[data-testid="render-waiting"]')).toBeNull();
    fixture.componentRef.setInput('waitingForSlot', true);
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="render-waiting"]')?.textContent).toContain(translated('episodes.render.waiting.slot'));
    expect(root.querySelector('[role="alert"]')).toBeNull();
    expect(root.textContent).not.toContain('errors.');
    fixture.componentRef.setInput('waitingForSlot', false);
    fixture.componentRef.setInput('showEmailNotice', false);
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="render-waiting"]')).toBeNull();
    expect(root.querySelector('.notice')).toBeNull();
  });

  it('clamps the bar, never exposes an unknown node stage, and formats hours', async () => {
    const { fixture, root } = await setup();
    // The container ignores an unknown node stage; the view receives only guaranteed stage keys.
    fixture.componentRef.setInput('steps', steps('running'));
    fixture.componentRef.setInput('percent', 250);
    fixture.componentRef.setInput('elapsedSec', 3723);
    await fixture.whenStable();
    expect(root.querySelector('[role="progressbar"]')?.getAttribute('aria-valuenow')).toBe('100');
    expect(root.querySelector<HTMLElement>('.progress-fill')?.style.width).toBe('100%');
    expect(root.textContent).not.toContain('v2');
    expect(root.textContent).toContain('1:02:03');
    // The store derives the elapsed value from wire dates: a non-finite one must still read as a duration, not NaN.
    fixture.componentRef.setInput('elapsedSec', Number.NaN);
    await fixture.whenStable();
    expect(root.textContent).not.toContain('NaN');
    expect(root.textContent).toContain(translated('episodes.render.timeline.elapsed', { duration: '0:00' }));
  });

  it.each(['failed', 'succeeded', 'cancelled'] as const)('hides progress for terminal %s', async (status) => {
    const { fixture, root } = await setup();
    const terminalSteps: RenderTimelineStep[] = steps('running').map((step) => ({
      ...step,
      state: status === 'succeeded' ? 'done' : step.key === 'queued' ? 'done' : step.key === 'running' && status === 'failed' ? 'failed' : 'todo',
    }));
    fixture.componentRef.setInput('steps', terminalSteps);
    await fixture.whenStable();
    expect(root.querySelector('[role="progressbar"]')).toBeNull();
  });

  it('does not render the cancel button unless cancellable, even when cancelling', async () => {
    const { fixture, root } = await setup();
    expect(root.querySelector('[data-testid="render-cancel"]')).toBeNull();
    fixture.componentRef.setInput('cancelling', true);
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="render-cancel"]')).toBeNull();
    fixture.componentRef.setInput('cancelling', false);
    fixture.componentRef.setInput('cancellable', true);
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="render-cancel"]')).not.toBeNull();
    fixture.componentRef.setInput('cancellable', false);
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="render-cancel"]')).toBeNull();
  });

  it.each(['vi', 'en'] as const)('renders one enabled cancel action and emits once per click in %s', async (lang) => {
    const { fixture, root } = await setup(lang);
    fixture.componentRef.setInput('cancellable', true);
    await fixture.whenStable();
    const buttons = root.querySelectorAll<HTMLButtonElement>('[data-testid="render-cancel"]');
    expect(buttons).toHaveLength(1);
    const button = buttons[0]!;
    expect(button.type).toBe('button');
    expect(button.hasAttribute('aria-disabled')).toBe(false);
    expect(button.hasAttribute('aria-busy')).toBe(false);
    expect(button.textContent?.trim()).toBe(translated('episodes.render.cancel.action'));
    let cancels = 0;
    onEmit(fixture.componentInstance.cancelRequest, () => cancels++);
    button.click();
    expect(cancels).toBe(1);
    button.click();
    expect(cancels).toBe(2);
  });

  it.each(['vi', 'en'] as const)('disables cancellation and announces the busy state without changing progress in %s', async (lang) => {
    const { fixture, root } = await setup(lang);
    fixture.componentRef.setInput('cancellable', true);
    await fixture.whenStable();
    const button = root.querySelector<HTMLButtonElement>('[data-testid="render-cancel"]')!;
    const live = root.querySelector('[aria-live="polite"]')!;
    const idleAnnouncement = live.textContent;
    const stage = root.querySelector('[aria-current="step"] strong')?.textContent;
    let cancels = 0;
    onEmit(fixture.componentInstance.cancelRequest, () => cancels++);
    fixture.componentRef.setInput('cancelling', true);
    await fixture.whenStable();
    const busyLabel = translated('episodes.render.cancel.busy');
    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(button.hasAttribute('disabled')).toBe(false);
    button.focus();
    expect(document.activeElement).toBe(button);
    expect(button.getAttribute('aria-busy')).toBe('true');
    expect(button.textContent?.trim()).toBe(busyLabel);
    expect(live.textContent).toContain(busyLabel);
    expect(live.textContent).not.toBe(idleAnnouncement);
    expect(root.querySelectorAll('[aria-live]')).toHaveLength(1);
    expect(root.querySelector('[aria-current="step"] strong')?.textContent).toBe(stage);
    button.click();
    const click = new MouseEvent('click', { bubbles: true, cancelable: true });
    button.dispatchEvent(click);
    expect(click.defaultPrevented).toBe(true);
    expect(cancels).toBe(0);
    fixture.componentRef.setInput('cancelling', false);
    await fixture.whenStable();
    expect(button.hasAttribute('aria-disabled')).toBe(false);
    expect(button.hasAttribute('aria-busy')).toBe(false);
    expect(button.textContent?.trim()).toBe(translated('episodes.render.cancel.action'));
    expect(live.textContent).toBe(idleAnnouncement);
  });

  it('ignores a stale click after the cancel action is no longer allowed', async () => {
    const { fixture, root } = await setup();
    fixture.componentRef.setInput('cancellable', true);
    await fixture.whenStable();
    const button = root.querySelector<HTMLButtonElement>('[data-testid="render-cancel"]')!;
    let cancels = 0;
    onEmit(fixture.componentInstance.cancelRequest, () => cancels++);
    fixture.componentRef.setInput('cancellable', false);
    button.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(cancels).toBe(0);
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="render-cancel"]')).toBeNull();
  });

  it.each(['vi', 'en'] as const)('renders and clears the supplied mapped error in an alert in %s', async (lang) => {
    const { fixture, root } = await setup(lang);
    fixture.componentRef.setInput('cancellable', true);
    fixture.componentRef.setInput('cancelErrorKey', 'errors.concurrency_conflict');
    await fixture.whenStable();
    expect(root.querySelectorAll('[role="alert"]')).toHaveLength(1);
    expect(root.querySelector('[role="alert"]')?.textContent?.trim()).toBe(translated('errors.concurrency_conflict'));
    fixture.componentRef.setInput('cancelErrorKey', 'errors.network_error');
    await fixture.whenStable();
    expect(root.querySelector('[role="alert"]')?.textContent?.trim()).toBe(translated('errors.network_error'));
    fixture.componentRef.setInput('cancelErrorKey', null);
    await fixture.whenStable();
    expect(root.querySelector('[role="alert"]')).toBeNull();
  });
});
