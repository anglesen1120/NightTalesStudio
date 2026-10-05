import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import type { SeriesListItem } from '../domain/series.model';
import { toSeriesRowViewModel } from '../domain/series-row.view-model';
import { SeriesRow } from './series-row';

const series: SeriesListItem = {
  id: 'series-one', title: 'Chuyện ma đêm khuya', topic: 'ghost', language: 'vi', aspect: 'landscape',
  voiceId: 'voice-one', styleTemplateId: 'style-one', autopilotEnabled: true,
  archivedAt: null, createdAt: '2026-10-05T00:00:00Z', updatedAt: '2026-10-05T00:00:00Z',
};
@Component({
  imports: [SeriesRow],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<ul><li nts-series-row [vm]="vm()" [busy]="busy()" (open)="opened.push($event)" (archive)="archived.push($event)"></li></ul>',
})
class SeriesRowHost {
  readonly vm = signal(toSeriesRowViewModel(series));
  readonly busy = signal(false);
  readonly opened: string[] = [];
  readonly archived: string[] = [];
}

afterEach(() => TestBed.resetTestingModule());
async function setup() {
  await TestBed.configureTestingModule({
    imports: [SeriesRowHost, TranslocoTestingModule.forRoot({
      langs: { vi: {}, 'series/vi': {
        topic: { ghost: 'Chuyện ma' }, aspect: { landscape: 'Ngang (video dài)' }, language: { vi: 'Tiếng Việt' },
        list: { archived: 'Đã lưu trữ' },
        row: { open: 'Mở', archive: 'Lưu trữ', openNamed: 'Mở series', archiveNamed: 'Lưu trữ series', autopilotOn: 'Tự động đang bật', autopilotOff: 'Tự động đang tắt' },
      } }, translocoConfig: { availableLangs: ['vi'], defaultLang: 'vi', fallbackLang: 'vi' }, preloadLangs: true,
    })], providers: ZONELESS_TEST_PROVIDERS,
  }).compileComponents();
  const fixture = TestBed.createComponent(SeriesRowHost);
  await fixture.whenStable();
  return { fixture, host: fixture.componentInstance, root: fixture.nativeElement as HTMLElement };
}

describe('SeriesRow', () => {
  it('ShowsChips_AndAutopilotChip', async () => {
    const { root } = await setup();
    expect(root.textContent).toContain('Chuyện ma');
    expect(root.textContent).toContain('Tiếng Việt');
    expect(root.textContent).toContain('Ngang (video dài)');
    expect(root.textContent).toContain('Tự động đang bật');
    expect(root.querySelector('nts-icon-check')).not.toBeNull();
  });

  it('ArchiveButton_HasAccessibleName', async () => {
    const { root } = await setup();
    const archive = root.querySelector<HTMLButtonElement>('button[aria-label^="Lưu trữ series"]');
    expect(archive?.getAttribute('aria-label')).toBe('Lưu trữ series Chuyện ma đêm khuya');
  });

  it.each(['{{title}}', '{{ x }}'])('LiteralTitle_%s_RendersVerbatim_InTextAndAriaLabels', async (title) => {
    // PS1 regression: a user-controlled title must never reach the Transloco transpiler as a param —
    // it re-interpolates substituted values, so "{{title}}" used to loop it forever.
    const { fixture, host, root } = await setup();
    host.vm.set(toSeriesRowViewModel({ ...series, title }));
    await fixture.whenStable();
    expect(root.querySelector<HTMLButtonElement>('.series-row__title')?.textContent?.trim()).toBe(title);
    expect(root.querySelector<HTMLButtonElement>('.series-row__title')?.getAttribute('aria-label')).toBe(`Mở series ${title}`);
    expect(root.querySelector<HTMLButtonElement>('button[aria-label^="Lưu trữ series"]')?.getAttribute('aria-label')).toBe(`Lưu trữ series ${title}`);
  });

  it('OpenButtons_HaveAccessibleNames', async () => {
    const { root } = await setup();
    const buttons = [...root.querySelectorAll<HTMLButtonElement>('button')].filter(
      (button) => button.getAttribute('aria-label')?.startsWith('Mở series'),
    );
    expect(buttons).toHaveLength(2);
    expect(buttons.every((button) => button.getAttribute('aria-label') === 'Mở series Chuyện ma đêm khuya')).toBe(true);
  });

  it('Open_Emits', async () => {
    const { host, root } = await setup();
    root.querySelector<HTMLButtonElement>('.series-row__actions button')!.click();
    expect(host.opened).toEqual(['series-one']);
  });

  it('RendersRuledListItem_NotMaterialCard', async () => {
    const { root } = await setup();
    expect(root.querySelector('ul > li[nts-series-row]')).not.toBeNull();
    expect(root.querySelector('mat-card')).toBeNull();
  });

  it('Archived_ShowsChip_AndNoArchiveAction', async () => {
    const { fixture, host, root } = await setup();
    host.vm.set(toSeriesRowViewModel({ ...series, archivedAt: '2026-10-05T01:00:00Z', autopilotEnabled: false }));
    await fixture.whenStable();
    expect(root.textContent).toContain('Đã lưu trữ');
    expect(root.textContent).toContain('Tự động đang tắt');
    expect(root.querySelector('button[aria-label^="Lưu trữ series"]')).toBeNull();
  });

  it('Busy_DisablesArchive_AndTitleIsEscaped', async () => {
    const { fixture, host, root } = await setup();
    host.vm.set(toSeriesRowViewModel({ ...series, title: '<img src=x onerror=alert(1)>' }));
    host.busy.set(true);
    await fixture.whenStable();
    expect(root.querySelector('img')).toBeNull();
    expect(root.textContent).toContain('<img src=x onerror=alert(1)>');
    expect(root.querySelector<HTMLButtonElement>('button[aria-label^="Lưu trữ series"]')?.disabled).toBe(true);
  });
});
