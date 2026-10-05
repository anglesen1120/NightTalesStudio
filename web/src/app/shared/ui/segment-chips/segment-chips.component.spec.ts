import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import { SegmentChipsComponent } from './segment-chips.component';
import { onEmit } from '../testing/on-emit';

const translations = TranslocoTestingModule.forRoot({
  langs: { vi: episodesVi, 'episodes/vi': episodesVi },
  translocoConfig: { availableLangs: ['vi'], defaultLang: 'vi', fallbackLang: 'vi', reRenderOnLangChange: true },
  preloadLangs: true,
});

async function setup(keywords: readonly string[] = ['Mưa']) {
  await TestBed.configureTestingModule({ imports: [SegmentChipsComponent, translations], providers: ZONELESS_TEST_PROVIDERS }).compileComponents();
  const fixture = TestBed.createComponent(SegmentChipsComponent);
  fixture.componentRef.setInput('keywords', keywords);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

afterEach(() => TestBed.resetTestingModule());

describe('SegmentChipsComponent', () => {
  it('emits add, edit and remove intents from labelled controls', async () => {
    const { root, component } = await setup(['Mưa']);
    const adds: void[] = [], edits: { index: number; value: string }[] = [], removes: number[] = [];
    onEmit(component.addKeyword, () => adds.push(undefined));
    onEmit(component.editKeyword, (value) => edits.push(value));
    onEmit(component.removeKeyword, (value) => removes.push(value));

    const input = root.querySelector<HTMLInputElement>('input[aria-label="Cảnh 1"]')!;
    input.value = 'Ngõ tối';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    root.querySelector<HTMLButtonElement>('.segment-chips__remove')!.click();
    [...root.querySelectorAll<HTMLButtonElement>('button')].find((button) => button.textContent?.includes('Thêm cảnh'))!.click();

    expect(edits).toEqual([{ index: 0, value: 'Ngõ tối' }]);
    expect(removes).toEqual([0]);
    expect(adds).toHaveLength(1);
  });

  it('keeps Add scene operable at two scenes and renders the textual limit hint', async () => {
    const { fixture, root, component } = await setup(['Mưa', 'Ngõ tối']);
    const adds: void[] = [];
    onEmit(component.addKeyword, () => adds.push(undefined));
    fixture.componentRef.setInput('limitHit', true);
    fixture.detectChanges();
    await fixture.whenStable();

    const add = [...root.querySelectorAll<HTMLButtonElement>('button')].find((button) => button.textContent?.includes('Thêm cảnh'))!;
    expect(add.disabled).toBe(false);
    add.click();
    expect(adds).toHaveLength(1);
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Mỗi đoạn cần 1–2 cảnh');
  });

  it('gives every scene input and removal control a text accessible name', async () => {
    const { root } = await setup(['Mưa', 'Ngõ tối']);
    expect([...root.querySelectorAll('input')].map((input) => input.getAttribute('aria-label'))).toEqual(['Cảnh 1', 'Cảnh 2']);
    const names = [...root.querySelectorAll<HTMLButtonElement>('.segment-chips__remove')].map((button) => button.querySelector('.sr-only')?.textContent?.replace(/\s+/g, ' ').trim());
    expect(names).toEqual(['Xoá cảnh Mưa', 'Xoá cảnh Ngõ tối']);
  });

  it('does not communicate the limit by colour alone', async () => {
    const { fixture, root } = await setup(['Mưa', 'Ngõ tối']);
    fixture.componentRef.setInput('limitHit', true);
    fixture.detectChanges();
    await fixture.whenStable();
    const status = root.querySelector<HTMLElement>('[role="status"]')!;
    expect(status.textContent).toContain('!');
    expect(status.textContent).toContain('Mỗi đoạn cần 1–2 cảnh');
  });
});
