import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom, of, Subject } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppErrorMapper, type AppError } from '@core/errors';
import { RealtimeService } from '@core/realtime';
import { ConfirmDialog, ConflictDialog, type ConflictDialogOutcome } from '@shared/ui';
import { onEmit } from '@shared/ui/testing/on-emit';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { FakeEpisodeRealtime } from '../testing/fake-episode-realtime';
import coreVi from '../../../../assets/i18n/core/vi.json';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import errorsVi from '../../../../assets/i18n/errors/vi.json';
import { VersionsStore, type RestoreOutcome } from '../application/versions.store';
import { EpisodeDetailStore } from '../application/episode-detail.store';
import type { StoryVersionDetail } from '../domain/story-version.model';
import { VersionsPanelHostComponent } from './versions-panel-host.component';

const previous: StoryVersionDetail = { versionId: 'older', version: 1, source: 'generated', createdAt: '2026-10-01T12:00:00Z', wordCount: 2, etag: 'older-etag', similarityScore: null, approved: false, isCurrent: false, body: 'old word', content: null, createdBy: 'owner' };
const current: StoryVersionDetail = { ...previous, versionId: 'current', version: 2, isCurrent: true, body: 'new word' };

function fakeStore(outcome: RestoreOutcome) {
  const pendingConflict = signal(false);
  const needsReopen = signal(false);
  const restoreError = signal<AppError | null>(null);
  const right = signal<StoryVersionDetail | null>(current);
  const loaded = signal(false);
  const restoredVersion = signal<number | null>(null);
  const restoring = signal(false);
  return {
    versions: signal([current, previous]), loaded, empty: signal(false),
    leftId: signal<string | null>('older'), rightId: signal<string | null>('current'),
    left: signal<StoryVersionDetail | null>(previous), right,
    diff: signal({ segments: [], added: 1, removed: 1, truncated: false }), sameSelected: signal(false),
    loading: signal(false), loadingSide: signal(null), sideError: signal(null), error: signal(null),
    restoring, canRestore: signal(true), editorDirty: signal(false), pendingConflict, needsReopen, restoreError, restoredVersion,
    consumeRestored: vi.fn(() => { const version = restoredVersion(); restoredVersion.set(null); return version; }),
    discardRestoredNotice: vi.fn(() => { if (!restoring()) restoredVersion.set(null); }),
    open: vi.fn(() => { loaded.set(true); return Promise.resolve(); }),
    applySelection: vi.fn(() => Promise.resolve()),
    select: vi.fn(() => Promise.resolve()), retryList: vi.fn(() => Promise.resolve()), retrySide: vi.fn(() => Promise.resolve()), reset: vi.fn(),
    resync: vi.fn(() => Promise.resolve()),
    restoreAsNewVersion: vi.fn((): Promise<RestoreOutcome> => {
      if (outcome === 'conflict') pendingConflict.set(true);
      if (outcome === 'needs_reopen') needsReopen.set(true);
      if (outcome === 'failed') restoreError.set({ code: 'network_error', kind: 'network', status: 0, fieldErrors: {} });
      if (outcome === 'restored') restoredVersion.set(3);
      return Promise.resolve(outcome);
    }),
    resolveConflict: vi.fn((choice: ConflictDialogOutcome) => {
      pendingConflict.set(false);
      if (choice === 'saveAsNew') { right.set({ ...current, versionId: 'restored', version: 3 }); restoredVersion.set(3); }
      return Promise.resolve();
    }),
  };
}

/** Waits out the host's 250 ms hub-coalescing window on real timers. */
// The tsconfig lib predates Promise.withResolvers, so the executor form is the only typed option here.
function settle(ms: number): Promise<void> {
  return new Promise((resolve) => { setTimeout(resolve, ms); });
}

async function setup(outcome: RestoreOutcome = 'restored', accepted = true, conflictChoice: ConflictDialogOutcome = 'cancel', deepLinked = true) {
  const store = fakeStore(outcome);
  const realtime = new FakeEpisodeRealtime();
  const episode = signal({ id: 'episode-1', status: 'story_approved' });
  const detailLoading = signal(false);
  const confirm = vi.fn(() => ({ afterClosed: () => of(accepted) }));
  const conflict = vi.fn(() => ({ afterClosed: () => of(conflictChoice) }));
  const snack = vi.fn();
  await TestBed.configureTestingModule({
    imports: [VersionsPanelHostComponent, TranslocoTestingModule.forRoot({
      langs: { vi: coreVi, 'core/vi': coreVi, 'episodes/vi': episodesVi, 'errors/vi': errorsVi },
      translocoConfig: { availableLangs: ['vi'], defaultLang: 'vi', fallbackLang: 'vi', reRenderOnLangChange: true }, preloadLangs: true,
    })],
    providers: [...ZONELESS_TEST_PROVIDERS,
      { provide: VersionsStore, useValue: store },
      { provide: EpisodeDetailStore, useValue: { episode: episode.asReadonly(), loading: detailLoading.asReadonly() } },
      { provide: RealtimeService, useValue: realtime },
      { provide: ConfirmDialog, useValue: { confirm } },
      { provide: ConflictDialog, useValue: { open: conflict } },
      { provide: MatSnackBar, useValue: { open: snack } },
      { provide: AppErrorMapper, useValue: { messageKey: () => 'errors.network_error', traceFooter: () => null, fieldMessageKey: () => 'errors.network_error' } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(VersionsPanelHostComponent);
  fixture.componentRef.setInput('episodeId', 'episode-1');
  if (deepLinked) {
    fixture.componentRef.setInput('initialLeft', 'older');
    fixture.componentRef.setInput('initialRight', 'current');
  }
  fixture.detectChanges(); await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement, store, episode, detailLoading, realtime, confirm, conflict, snack };
}

afterEach(() => TestBed.resetTestingModule());

describe('VersionsPanelHostComponent', () => {
  it('opens once with deep-link ids, re-opens on a changed deep link, emits selection ids, and preserves page state when unmounted', async () => {
    const { fixture, store } = await setup();
    expect(store.open).toHaveBeenCalledExactlyOnceWith('episode-1', { left: 'older', right: 'current' });
    expect(store.discardRestoredNotice).toHaveBeenCalledTimes(1); // mount: no stale notice survives into this open
    const ids: { left: string | null; right: string | null }[] = [];
    onEmit(fixture.componentInstance.selectionChange, (value) => ids.push(value));
    store.leftId.set('another');
    fixture.detectChanges(); await fixture.whenStable();
    expect(ids).toEqual([{ left: 'another', right: 'current' }]);
    fixture.componentRef.setInput('initialLeft', 'another');
    fixture.detectChanges(); await fixture.whenStable();
    // A later deep link only moves the pair: no second open (and no list read), which the page's own echo relies on.
    expect(store.open).toHaveBeenCalledTimes(1);
    expect(store.applySelection).toHaveBeenCalledExactlyOnceWith({ left: 'another', right: 'current' });
    fixture.destroy();
    expect(store.reset).not.toHaveBeenCalled();
    expect(store.discardRestoredNotice).toHaveBeenCalledTimes(2); // destroy: no host left to announce it
  });

  it('drops a restore notice no host consumed instead of announcing it on the next mount', async () => {
    const { fixture, store, snack } = await setup();
    fixture.destroy();
    store.restoredVersion.set(3); // a restore that finished while the panel was closed
    const reopened = TestBed.createComponent(VersionsPanelHostComponent);
    reopened.componentRef.setInput('episodeId', 'episode-1');
    reopened.detectChanges(); await reopened.whenStable(); reopened.detectChanges(); await reopened.whenStable();
    expect(store.discardRestoredNotice).toHaveBeenCalledTimes(3); // mount + destroy of the first host, mount of this one
    expect(store.restoredVersion()).toBeNull();
    expect(snack).not.toHaveBeenCalled();
  });

  it('confirms the source version and preservation of history, using dirty-specific copy', async () => {
    const { fixture, root, store, confirm, snack } = await setup();
    store.editorDirty.set(true);
    root.querySelector<HTMLButtonElement>('.versions__restore')!.click();
    await fixture.whenStable();
    expect(confirm).toHaveBeenCalledWith(expect.objectContaining({
      titleKey: 'episodes.versions.restoreConfirm.title', bodyKey: 'episodes.versions.restoreConfirm.dirtyBody',
      subject: TestBed.inject(TranslocoService).translate('episodes.versions.version', { version: 1 }),
      confirmLabelKey: 'episodes.versions.restoreConfirm.confirm', destructive: true,
    }), expect.anything());
    expect(store.restoreAsNewVersion).toHaveBeenCalledTimes(1);
    expect(snack).toHaveBeenCalledWith(TestBed.inject(TranslocoService).translate('episodes.versions.restored'), undefined, { duration: 5000 });
  });

  it('does not restore after Cancel, and guards unavailable/current/read-only state', async () => {
    const { fixture, root, store, confirm } = await setup('restored', false);
    root.querySelector<HTMLButtonElement>('.versions__restore')!.click(); await fixture.whenStable();
    expect(store.restoreAsNewVersion).not.toHaveBeenCalled();
    store.canRestore.set(false);
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector<HTMLButtonElement>('.versions__restore')!.disabled).toBe(true);
    root.querySelector<HTMLButtonElement>('.versions__restore')!.click();
    expect(confirm).toHaveBeenCalledTimes(1);
  });

  it.each(['reload', 'saveAsNew', 'cancel'] as const)('maps the conflict dialog %s choice without an implicit overwrite', async (choice) => {
    const { fixture, root, store, conflict, snack } = await setup('conflict', true, choice);
    root.querySelector<HTMLButtonElement>('.versions__restore')!.click(); await fixture.whenStable();
    expect(conflict).toHaveBeenCalledWith(expect.objectContaining({ messageKey: 'episodes.versions.conflictBody', saveAsNewLabel: 'errors.conflict.saveAsNew' }), expect.anything());
    expect(store.resolveConflict).toHaveBeenCalledExactlyOnceWith(choice);
    expect(store.restoreAsNewVersion).toHaveBeenCalledTimes(1);
    expect(snack).toHaveBeenCalledTimes(choice === 'saveAsNew' ? 1 : 0);
  });

  it.each(['needs_reopen', 'failed'] as const)('renders %s inline and forwards the real reopen intent', async (outcome) => {
    const { fixture, root, snack } = await setup(outcome);
    root.querySelector<HTMLButtonElement>('.versions__restore')!.click(); await fixture.whenStable(); fixture.detectChanges();
    expect(snack).not.toHaveBeenCalled();
    if (outcome === 'needs_reopen') {
      expect(root.querySelector('.versions__alert')).not.toBeNull();
      let requests = 0;
      onEmit(fixture.componentInstance.reopenRequested, () => requests++);
      root.querySelector<HTMLButtonElement>('.versions__reopen')!.click();
      expect(requests).toBe(1);
    } else expect(root.querySelector('nts-form-error-alert')).not.toBeNull();
  });

  it('clears the reopen notice when the same episode leaves approved without discarding comparison state', async () => {
    const { fixture, root, store, episode, detailLoading } = await setup('needs_reopen');
    root.querySelector<HTMLButtonElement>('.versions__restore')!.click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(store.needsReopen()).toBe(true);
    detailLoading.set(true);
    episode.set({ id: 'episode-1', status: 'story_review' });
    fixture.detectChanges(); await fixture.whenStable();
    expect(store.needsReopen()).toBe(true);
    episode.set({ id: 'episode-1', status: 'story_approved' });
    detailLoading.set(false);
    fixture.detectChanges(); await fixture.whenStable();
    expect(store.needsReopen()).toBe(true);
    episode.set({ id: 'other-episode', status: 'story_review' });
    fixture.detectChanges(); await fixture.whenStable();
    expect(store.needsReopen()).toBe(true);
    episode.set({ id: 'episode-1', status: 'story_review' });
    fixture.detectChanges(); await fixture.whenStable();
    expect(store.needsReopen()).toBe(false);
    expect(root.querySelector('.versions__alert')).toBeNull();
    expect(store.reset).not.toHaveBeenCalled();
    expect(store.leftId()).toBe('older');
    expect(store.rightId()).toBe('current');
  });

  it('ignores a late confirmation after the host closes', async () => {
    const { fixture, root, store, confirm } = await setup();
    const decision = new Subject<boolean>();
    confirm.mockReturnValue({ afterClosed: () => decision });
    root.querySelector<HTMLButtonElement>('.versions__restore')!.click();
    fixture.destroy();
    decision.next(true); decision.complete();
    await Promise.resolve(); await Promise.resolve();
    expect(store.restoreAsNewVersion).not.toHaveBeenCalled();
  });

  it('passes undefined rather than an explicit null selection when opened without query ids', async () => {
    const { store } = await setup('restored', true, 'cancel', false);
    expect(store.open).toHaveBeenCalledExactlyOnceWith('episode-1', undefined);
  });

  it('finishes a restore after host destruction and announces once from the remounted host', async () => {
    const { fixture, root, store, snack } = await setup();
    const completion = new Subject<RestoreOutcome>();
    store.restoreAsNewVersion.mockImplementation(() => firstValueFrom(completion).then((outcome) => {
      store.restoredVersion.set(3);
      return outcome;
    }));
    root.querySelector<HTMLButtonElement>('.versions__restore')!.click();
    await Promise.resolve(); await Promise.resolve();
    expect(store.restoreAsNewVersion).toHaveBeenCalledTimes(1);
    const result = store.restoreAsNewVersion.mock.results[0]!.value as Promise<RestoreOutcome>;
    fixture.destroy();
    expect(store.reset).not.toHaveBeenCalled();
    const remounted = TestBed.createComponent(VersionsPanelHostComponent);
    remounted.componentRef.setInput('episodeId', 'episode-1');
    remounted.detectChanges(); await remounted.whenStable();
    completion.next('restored'); completion.complete();
    expect(await result).toBe('restored');
    remounted.detectChanges(); await remounted.whenStable();
    expect(snack).toHaveBeenCalledTimes(1);
    expect(store.restoredVersion()).toBeNull();
    expect(store.consumeRestored).toHaveBeenCalledTimes(1);
    remounted.destroy();
    const reopened = TestBed.createComponent(VersionsPanelHostComponent);
    reopened.componentRef.setInput('episodeId', 'episode-1');
    reopened.detectChanges(); await reopened.whenStable();
    expect(snack).toHaveBeenCalledTimes(1);
    expect(store.reset).not.toHaveBeenCalled();
  });

  it('refreshes the list once per coalesced hub burst and stops listening on destroy', async () => {
    const { fixture, store, realtime } = await setup();
    expect(realtime.subscriptions).toHaveLength(1);

    realtime.emit('episode-1');
    realtime.emit('episode-1');
    await settle(320);
    expect(store.resync).toHaveBeenCalledTimes(1);

    fixture.destroy();
    expect(realtime.subscriptions[0]?.unsubscribed).toBe(true);
    realtime.emit('episode-1');
    await settle(320);
    expect(store.resync).toHaveBeenCalledTimes(1);
  });

});
