import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { MAX_DIFF_TOKENS, type WordDiff } from '@shared/util/text-diff';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { onEmit } from '../testing/on-emit';
import { DiffViewComponent } from './diff-view.component';

async function setup(left = 'old word', right = 'new word') {
  await TestBed.configureTestingModule({ imports: [DiffViewComponent], providers: ZONELESS_TEST_PROVIDERS }).compileComponents();
  const fixture = TestBed.createComponent(DiffViewComponent);
  fixture.componentRef.setInput('left', left);
  fixture.componentRef.setInput('right', right);
  fixture.componentRef.setInput('label', 'diff-label');
  fixture.componentRef.setInput('addedLabel', 'added-key');
  fixture.componentRef.setInput('removedLabel', 'removed-key');
  const results: WordDiff[] = [];
  onEmit(fixture.componentInstance.rendered, (value) => results.push(value));
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement, results };
}

afterEach(() => TestBed.resetTestingModule());

describe('DiffViewComponent', () => {
  it('emits counts once per text pair, not on a template refresh or label change', async () => {
    const { fixture, root, results } = await setup();
    expect(results).toHaveLength(1);
    expect(results[0]).toMatchObject({ added: 1, removed: 1, truncated: false });
    expect(root.querySelector('[data-kind="insert"]')?.textContent).toContain('+');
    expect(root.querySelector('[data-kind="delete"]')?.textContent).toContain('−');
    expect(root.querySelector('[data-kind="insert"] .sr-only')?.textContent).toContain('added-key');
    expect(root.querySelector('[data-kind="delete"] .sr-only')?.textContent).toContain('removed-key');
    expect(root.querySelector('[role="region"]')?.getAttribute('tabindex')).toBe('0');
    fixture.detectChanges();
    fixture.componentRef.setInput('label', 'other-label');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(results).toHaveLength(1);
    fixture.componentRef.setInput('right', 'old word');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(results).toHaveLength(2);
    expect(results[1]).toMatchObject({ added: 0, removed: 0 });
  });

  it('renders untrusted markup as visible text without creating executable nodes', async () => {
    const payload = '<script>alert(1)</script>';
    const { root } = await setup('', payload);
    expect(root.textContent).toContain(payload);
    expect(root.querySelector('script')).toBeNull();
    expect(root.querySelector('[data-kind="insert"]')?.textContent).toContain(payload);
  });

  it('renders generic add/remove/change rows with labels and before/after text', async () => {
    const { fixture, root, results } = await setup();
    fixture.componentRef.setInput('mode', 'rows');
    fixture.componentRef.setInput('rows', [
      { kind: 'add', label: 'added-row', after: '<b>safe</b>' },
      { kind: 'remove', label: 'removed-row', before: 'before' },
      { kind: 'change', label: 'changed-row', before: 'old', after: 'new' },
    ]);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelectorAll('.diff__row')).toHaveLength(3);
    expect(root.querySelector('[data-kind="change"]')?.textContent).toContain('changed-row');
    expect(root.querySelector('[data-kind="change"] del')?.textContent).toContain('old');
    expect(root.querySelector('[data-kind="change"] ins')?.textContent).toContain('new');
    expect(root.querySelector('b')).toBeNull();
    expect(results.at(-1)).toMatchObject({ added: 2, removed: 2, truncated: false });
  });

  it('shows the supplied localized truncation notice instead of a partial diff', async () => {
    const { fixture, root, results } = await setup('word '.repeat(MAX_DIFF_TOKENS + 1), 'short');
    fixture.componentRef.setInput('truncatedNotice', 'truncated-key');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(results[0]?.truncated).toBe(true);
    expect(root.querySelector('.diff__notice')?.textContent).toContain('truncated-key');
    expect(root.querySelector('[data-kind]')).toBeNull();
    expect(results).toHaveLength(1);
  });
});
