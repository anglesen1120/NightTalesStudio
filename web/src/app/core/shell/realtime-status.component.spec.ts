import { TestBed } from '@angular/core/testing';
import type { RealtimeState } from '@core/realtime';
import { describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { onEmit } from '@shared/ui/testing/on-emit';
import { uiTestingTranslations } from '@shared/ui/testing/ui-testing-translations';
import { RealtimeStatusComponent } from './realtime-status.component';

async function createRealtimeStatus(state: RealtimeState) {
  await TestBed.configureTestingModule({
    imports: [RealtimeStatusComponent, uiTestingTranslations('en')],
    providers: ZONELESS_TEST_PROVIDERS,
  }).compileComponents();
  const fixture = TestBed.createComponent(RealtimeStatusComponent);
  fixture.componentRef.setInput('state', state);
  fixture.detectChanges();
  await fixture.whenStable();
  return fixture;
}

describe('RealtimeStatusComponent', () => {
  it('keeps an empty polite live region for connected, idle and stopped states', async () => {
    const fixture = await createRealtimeStatus('connected');
    const root = fixture.nativeElement as HTMLElement;
    expect(root.getAttribute('aria-live')).toBe('polite');
    expect(root.textContent?.trim()).toBe('');

    for (const state of ['idle', 'stopped'] satisfies RealtimeState[]) {
      fixture.componentRef.setInput('state', state);
      await fixture.whenStable();
      expect(root.textContent?.trim()).toBe('');
    }
  });

  it('renders reconnecting with decorative icon and visible text', async () => {
    const fixture = await createRealtimeStatus('backoff');
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('Reconnecting…');
    expect(root.querySelector('nts-icon-clock')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('renders paused and emits resumeRequested once', async () => {
    const fixture = await createRealtimeStatus('paused');
    let emissions = 0;
    onEmit(fixture.componentInstance.resumeRequested, () => emissions++);
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('Live updates paused');
    root.querySelector<HTMLButtonElement>('button')?.click();
    await fixture.whenStable();
    expect(emissions).toBe(1);
  });
});
