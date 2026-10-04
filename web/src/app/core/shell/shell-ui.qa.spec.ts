import { TestBed } from '@angular/core/testing';
import type { RealtimeState } from '@core/realtime';
import type { MeSummary } from '@core/session';
import { describe, expect, it } from 'vitest';
import { uiTestingTranslations } from '@shared/ui/testing/ui-testing-translations';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { RealtimeStatusComponent } from './realtime-status.component';
import { UserMenuComponent } from './user-menu.component';

// QA (10.2.6-T1): realtime strip announcements and user-menu rendering of hostile / very long identity text.

async function realtime(state: RealtimeState) {
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

describe('RealtimeStatusComponent (QA)', () => {
  it('connecting → backoff keeps the same DOM node (no re-announcement) and the same text', async () => {
    const fixture = await realtime('connecting');
    const root = fixture.nativeElement as HTMLElement;
    const node = root.querySelector('.realtime-status');
    expect(node).not.toBeNull();
    fixture.componentRef.setInput('state', 'backoff');
    await fixture.whenStable();
    expect(root.querySelector('.realtime-status')).toBe(node);
    expect(root.textContent).toContain('Reconnecting…');
  });

  it('connected → paused → connected removes the strip text and the button each time', async () => {
    const fixture = await realtime('connected');
    const root = fixture.nativeElement as HTMLElement;
    for (let i = 0; i < 3; i++) {
      fixture.componentRef.setInput('state', 'paused');
      await fixture.whenStable();
      expect(root.querySelectorAll('button')).toHaveLength(1);
      fixture.componentRef.setInput('state', 'connected');
      await fixture.whenStable();
      expect(root.textContent?.trim()).toBe('');
      expect(root.querySelectorAll('button')).toHaveLength(0);
    }
  });

  it('the live region host persists (role=status, aria-live=polite, atomic) across every state', async () => {
    const fixture = await realtime('connected');
    const root = fixture.nativeElement as HTMLElement;
    for (const state of ['idle', 'connecting', 'connected', 'backoff', 'paused', 'stopped'] satisfies RealtimeState[]) {
      fixture.componentRef.setInput('state', state);
      await fixture.whenStable();
      expect(root.getAttribute('role')).toBe('status');
      expect(root.getAttribute('aria-live')).toBe('polite');
    }
  });

  it('the backoff strip has an icon AND text, never colour-only', async () => {
    const fixture = await realtime('backoff');
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('nts-icon-clock')).not.toBeNull();
    expect(root.textContent?.trim().length).toBeGreaterThan(0);
  });
});

const base: MeSummary = { id: 'u', displayName: 'An', email: 'an@example.test', uiLocale: 'vi', role: 'user', features: [] };

async function menu(me: MeSummary | null) {
  await TestBed.configureTestingModule({
    imports: [UserMenuComponent, uiTestingTranslations('en')],
    providers: ZONELESS_TEST_PROVIDERS,
  }).compileComponents();
  const fixture = TestBed.createComponent(UserMenuComponent);
  fixture.componentRef.setInput('me', me);
  fixture.detectChanges();
  await fixture.whenStable();
  return fixture;
}

describe('UserMenuComponent (QA)', () => {
  it('a 500-char name with markup-like text is plain text, full value in the menu header, no injected elements', async () => {
    const name = `<img src=x onerror=alert(1)>${'Nguyễn '.repeat(70)}`;
    const email = `${'very.long.local.part'.repeat(10)}@example.test<script>x</script>`;
    const fixture = await menu({ ...base, displayName: name, email });
    const root = fixture.nativeElement as HTMLElement;
    root.querySelector<HTMLButtonElement>('button')?.click();
    await fixture.whenStable();
    expect(root.querySelector('img')).toBeNull();
    expect(root.querySelector('script')).toBeNull();
    const header = document.querySelector<HTMLElement>('.user-menu__header');
    expect(header?.querySelector('img, script')).toBeNull();
    expect(header?.querySelector('.user-menu__full-name')?.textContent).toBe(name);
    expect(header?.querySelector('.user-menu__email')?.textContent).toBe(email);
  });

  it('an astral-plane (emoji) first character yields a well-formed avatar initial', async () => {
    const fixture = await menu({ ...base, displayName: '😀 Bob' });
    const avatar = (fixture.nativeElement as HTMLElement).querySelector('.user-menu__avatar')?.textContent ?? '';
    expect(/[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/u.test(avatar)).toBe(false);
  });

  it.each(['', '   ', '\u200B'])('display name %j never produces an empty avatar or a crash', async (displayName) => {
    const fixture = await menu({ ...base, displayName });
    const avatar = (fixture.nativeElement as HTMLElement).querySelector('.user-menu__avatar');
    expect(avatar?.textContent?.length ?? 0).toBeGreaterThan(0);
  });

  it('the trigger never renders token-like material from the summary', async () => {
    const fixture = await menu(base);
    const html = (fixture.nativeElement as HTMLElement).innerHTML.toLowerCase();
    expect(html).not.toContain('token');
    expect(html).not.toContain('bearer');
  });
});
