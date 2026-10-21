import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { onEmit } from '@shared/ui/testing/on-emit';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import { researchTranslations } from '../../testing/research-ui.testing';
import { SourceAddFormComponent } from './source-add-form.component';
afterEach(() => TestBed.resetTestingModule());
async function setup() {
  await TestBed.configureTestingModule({ imports: [SourceAddFormComponent, researchTranslations()], providers: [...ZONELESS_TEST_PROVIDERS] }).compileComponents();
  const fixture = TestBed.createComponent(SourceAddFormComponent); await fixture.whenStable();
  return { fixture, form: fixture.componentInstance, root: fixture.nativeElement as HTMLElement };
}
describe('SourceAddFormComponent', () => {
  it('activates one field and blocks notes longer than 3000 with an error counter', async () => {
    const { fixture, form, root } = await setup(); let notes = 0; onEmit(form.addNote, () => notes++);
    form.choose('note'); form.change('n'.repeat(3001)); await fixture.whenStable();
    expect(root.querySelector('input')).toBeNull(); expect(root.querySelector('textarea')).not.toBeNull();
    expect(root.querySelector('nts-char-counter')?.classList.contains('char-counter--over')).toBe(true);
    expect(root.querySelector<HTMLButtonElement>('button[type=submit]')?.disabled).toBe(true);
    form.submit(); expect(notes).toBe(0); form.change('n'.repeat(3000)); form.submit(); expect(notes).toBe(1);
  });
  it('links canonical URL and domain errors to the input and clears them on edit', async () => {
    const { fixture, form, root } = await setup(); let edits = 0; onEmit(form.edited, () => edits++);
    for (const key of ['errors.source_domain_not_allowed', 'errors.source_url_invalid']) {
      fixture.componentRef.setInput('errorKey', key); await fixture.whenStable();
      const input = root.querySelector('input')!; expect(input.getAttribute('aria-invalid')).toBe('true');
      expect(input.getAttribute('aria-describedby')).toContain(form.fieldId + '-error'); expect(root.querySelector('.field-error')?.textContent).not.toBe('');
    }
    form.change('https://vnexpress.net/story'); expect(edits).toBe(1);
  });
  it('blocks unsafe URLs, cap and double submission while busy', async () => {
    const { fixture, form, root } = await setup(); let links = 0; onEmit(form.addUrl, () => links++);
    form.change('javascript:alert(1)'); form.submit(); expect(links).toBe(0);
    form.change('https://vnexpress.net/story'); fixture.componentRef.setInput('canAdd', false); await fixture.whenStable();
    expect(root.querySelector<HTMLButtonElement>('button[type=submit]')?.disabled).toBe(true); form.submit(); expect(links).toBe(0);
    fixture.componentRef.setInput('canAdd', true); fixture.componentRef.setInput('busy', true); await fixture.whenStable(); form.submit(); expect(links).toBe(0);
  });
  it('points duplicates at the existing row and renders RetryCountdown', async () => {
    const { fixture, root } = await setup(); fixture.componentRef.setInput('duplicateSourceId', 'source-a'); fixture.componentRef.setInput('retryAfterSec', 60); await fixture.whenStable();
    expect(root.querySelector('a')?.getAttribute('href')).toBe('#research-source-source-a'); expect(root.querySelector('nts-retry-countdown')).not.toBeNull();
  });
  it('keeps Retry-After blocking after editing clears a server error', async () => {
    const { fixture, form, root } = await setup();
    fixture.componentRef.setInput('retryAfterSec', 60);
    await fixture.whenStable();
    form.change('https://vnexpress.net/story');
    fixture.componentRef.setInput('retryAfterSec', null);
    await fixture.whenStable();
    expect(root.querySelector<HTMLButtonElement>('button[type=submit]')?.disabled).toBe(true);
    form.finishRetry(); await fixture.whenStable();
    expect(root.querySelector<HTMLButtonElement>('button[type=submit]')?.disabled).toBe(false);
  });
});
