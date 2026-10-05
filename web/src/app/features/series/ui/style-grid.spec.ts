import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { DeferBlockBehavior, DeferBlockState, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import type { Topic } from '../domain/series.model';
import type { StyleTemplate } from '../domain/style-template.model';
import { StyleGrid } from './style-grid';

const templates: readonly StyleTemplate[] = [
  { id: 'ink', name: 'dark-folklore-ink', topicHint: 'ghost', previewUrl: '/media/ink.webp' },
  { id: 'water', name: 'watercolor-night', topicHint: null, previewUrl: null },
  { id: 'noir', name: 'noir-sketch', topicHint: 'true_crime', previewUrl: '/media/noir.webp' },
  { id: 'case', name: 'case-file-illustration', topicHint: 'true_crime', previewUrl: null },
];
const viCopy = { field: { style: 'Phong cách hình ảnh' }, style: {
  'dark-folklore-ink': { label: 'Mực tàu dân gian u tối', hint: 'Cảnh dân gian vẽ mực' },
  'watercolor-night': { label: 'Màu nước đêm', hint: 'Màu nước dịu' },
  'noir-sketch': { label: 'Phác thảo noir', hint: 'Chỉ cảnh vật và đồ vật' },
  'case-file-illustration': { label: 'Minh hoạ hồ sơ vụ án', hint: 'Không có khuôn mặt' },
  selected: 'Đã chọn', inactive: 'Phong cách này không còn khả dụng', trueCrimeNotice: 'Chỉ minh hoạ, không có khuôn mặt người thật.',
} };
const enCopy = { field: { style: 'Visual style' }, style: {
  'dark-folklore-ink': { label: 'Dark folklore ink', hint: 'Ink-wash folklore scenes' },
  'watercolor-night': { label: 'Watercolor night', hint: 'Soft night watercolours' },
  'noir-sketch': { label: 'Noir sketch', hint: 'Places and objects only' },
  'case-file-illustration': { label: 'Case-file illustration', hint: 'No faces' },
  selected: 'Selected', inactive: 'This style is no longer available', trueCrimeNotice: 'Illustrations only, without real faces.',
} };

@Component({
  imports: [StyleGrid],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<nts-style-grid [templates]="templates()" [topic]="topic()" [selectedId]="selectedId()" [disabled]="disabled()" [inactiveTemplateId]="inactiveId()" (selected)="selectedId.set($event); emitted.push($event)" />',
})
class StyleGridHost {
  readonly templates = signal(templates);
  readonly topic = signal<Topic>('ghost');
  readonly selectedId = signal<string | null>(null);
  readonly disabled = signal(false);
  readonly inactiveId = signal<string | null>(null);
  readonly emitted: string[] = [];
}

afterEach(() => TestBed.resetTestingModule());

async function setup() {
  await TestBed.configureTestingModule({
    imports: [StyleGridHost, TranslocoTestingModule.forRoot({
      langs: { vi: {}, en: {}, 'series/vi': viCopy, 'series/en': enCopy },
      translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: 'vi', fallbackLang: 'vi', reRenderOnLangChange: true },
      preloadLangs: true,
    })],
    providers: ZONELESS_TEST_PROVIDERS,
    deferBlockBehavior: DeferBlockBehavior.Manual,
  }).compileComponents();
  const fixture = TestBed.createComponent(StyleGridHost);
  await fixture.whenStable();
  return { fixture, host: fixture.componentInstance, root: fixture.nativeElement as HTMLElement };
}

describe('StyleGrid', () => {
  it('RadioGroupSemantics', async () => {
    const { root } = await setup();
    expect(root.querySelector('[role="radiogroup"]')?.getAttribute('aria-label')).toBe('Phong cách hình ảnh');
    expect(root.querySelectorAll('input[type="radio"]')).toHaveLength(2);
    const radios = Array.from(root.querySelectorAll<HTMLInputElement>('input[type="radio"]'));
    expect(radios[0]?.name).toBe(radios[1]?.name);
  });

  it('AltIsLabel', async () => {
    const { fixture, root } = await setup();
    const blocks = await fixture.getDeferBlocks();
    await blocks[0]!.render(DeferBlockState.Complete);
    await fixture.whenStable();
    expect(root.querySelector('img')?.alt).toBe('Mực tàu dân gian u tối');
    expect(root.querySelector('img')?.getAttribute('loading')).toBe('lazy');
    TestBed.inject(TranslocoService).setActiveLang('en');
    await fixture.whenStable();
    expect(root.querySelector('img')?.alt).toBe('Dark folklore ink');
    expect(root.textContent).toContain('Watercolor night');
    expect(root.textContent).toContain('Soft night watercolours');
  });

  it('TrueCrimeNotice_OnlyForTrueCrime', async () => {
    const { fixture, host, root } = await setup();
    expect(root.querySelector('[role="note"]')).toBeNull();
    host.topic.set('true_crime');
    await fixture.whenStable();
    expect(root.querySelector('[role="note"]')?.textContent).toContain('Chỉ minh hoạ');
    expect(root.querySelectorAll('input[type="radio"]')).toHaveLength(2);
    host.topic.set('spiritual');
    await fixture.whenStable();
    expect(root.querySelector('[role="note"]')).toBeNull();
  });

  it('Selection_EmitsOnce', async () => {
    const { fixture, host, root } = await setup();
    const input = root.querySelector<HTMLInputElement>('input[type="radio"]')!;
    input.click();
    await fixture.whenStable();
    input.click();
    await fixture.whenStable();
    expect(host.emitted).toEqual(['ink']);
    expect(root.querySelector('nts-icon-check')).not.toBeNull();
  });

  it('FailedImage_NoBrokenIcon', async () => {
    const { fixture, root } = await setup();
    const blocks = await fixture.getDeferBlocks();
    await blocks[0]!.render(DeferBlockState.Complete);
    await fixture.whenStable();
    root.querySelector('img')!.dispatchEvent(new Event('error'));
    await fixture.whenStable();
    expect(root.querySelector('img')).toBeNull();
    expect(root.textContent).toContain('Mực tàu dân gian u tối');
    expect(root.textContent).toContain('Màu nước đêm');
  });

  it('TopicChange_ReFilters', async () => {
    const { fixture, host, root } = await setup();
    expect(Array.from(root.querySelectorAll<HTMLInputElement>('input')).map((input) => input.value)).toEqual(['ink', 'water']);
    host.topic.set('true_crime');
    await fixture.whenStable();
    expect(Array.from(root.querySelectorAll<HTMLInputElement>('input')).map((input) => input.value)).toEqual(['noir', 'case']);
  });

  it('ArrowKeys_SelectAndFocus_IgnoreBubbledControls', async () => {
    const { fixture, host, root } = await setup();
    const radios = root.querySelectorAll<HTMLInputElement>('input[type="radio"]');
    radios[0]!.focus();
    radios[0]!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true }));
    await fixture.whenStable();
    expect(host.emitted).toEqual(['water']);
    expect(document.activeElement).toBe(radios[1]);
    const nested = document.createElement('button');
    root.querySelector('[role="radiogroup"]')!.append(nested);
    nested.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true }));
    expect(host.emitted).toEqual(['water']);
  });

  it('Disabled_BlocksSelection', async () => {
    const { fixture, host, root } = await setup();
    host.disabled.set(true);
    await fixture.whenStable();
    const input = root.querySelector<HTMLInputElement>('input')!;
    expect(input.disabled).toBe(true);
    input.dispatchEvent(new Event('change', { bubbles: true }));
    expect(host.emitted).toEqual([]);
  });

  it('InactiveFallback_ShowsIdAndMessage_WithoutEmptyTranslationKey', async () => {
    const { fixture, host, root } = await setup();
    host.templates.set([...templates, { id: 'stored-style', name: '', topicHint: null, previewUrl: null }]);
    host.inactiveId.set('stored-style');
    await fixture.whenStable();
    expect(root.textContent).toContain('stored-style');
    expect(root.textContent).toContain('Phong cách này không còn khả dụng');
    expect(root.textContent).not.toContain('series.style..');
  });
});
