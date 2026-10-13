import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { APP_ERROR_DEV_MODE } from '@core/errors/app-error-mapper';
import { RealtimeService } from '@core/realtime';
import { SessionStore } from '@core/session';
import { vi } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import type { EpisodeDetail } from '../domain/episode.model';
import type { QualityHint, StorySnapshot } from '../domain/story.model';
import { EpisodesRepository } from '../domain/episodes.repository';
import { StoryRepository } from '../domain/story.repository';
import { FakeEpisodeRealtime } from '../testing/fake-episode-realtime';
import { EPISODE_ID, FakeEpisodesRepository, episodeDetail } from '../testing/fake-episodes.repository';
import { FakeStoryRepository, storyContent, storySnapshot } from '../testing/fake-story.repository';
import { EpisodeDetailStore } from './episode-detail.store';
import { StoryEditorStore } from './story-editor.store';

const ADVISORY = { severity: 'advisory', kind: 'objective', evidence: '', repairDirection: null } as const;
const HINTS: readonly QualityHint[] = [
  { ruleId: 'hook-short', ...ADVISORY, segment: 'p:2', messageKey: 'quality.hook_short' },
  { ruleId: 'length-band', ...ADVISORY, segment: 'p:3', messageKey: 'quality.length_out_of_range' },
];
/** One hint on paragraph 2 (`p:1`): the paragraph the move-while-buffered undo test deletes. */
const HINT_ON_P1: readonly QualityHint[] = [
  { ruleId: 'hook-short', ...ADVISORY, segment: 'p:1', messageKey: 'quality.hook_short' },
];

function paragraph(snapshot: StorySnapshot, index: number): string {
  return snapshot.content.paragraphs[index]?.text ?? '';
}

function setup(snapshot: StorySnapshot = storySnapshot(), actions: readonly string[] = ['edit_story'], detailOverrides: Partial<EpisodeDetail> = {}) {
  const episodes = new FakeEpisodesRepository();
  episodes.scriptGet(() => Promise.resolve(episodeDetail({ currentStory: snapshot, hasCurrentStory: snapshot !== null, nextActions: actions, status: 'story_review', ...detailOverrides })));
  const stories = new FakeStoryRepository();
  const realtime = new FakeEpisodeRealtime();
  TestBed.configureTestingModule({ providers: [
    ...ZONELESS_TEST_PROVIDERS, EpisodeDetailStore, StoryEditorStore,
    { provide: EpisodesRepository, useValue: episodes }, { provide: StoryRepository, useValue: stories },
    { provide: RealtimeService, useValue: realtime }, { provide: SessionStore, useValue: { generation: signal(0) } },
    { provide: APP_ERROR_DEV_MODE, useValue: false },
  ] });
  return { store: TestBed.inject(StoryEditorStore), detail: TestBed.inject(EpisodeDetailStore), episodes, stories, realtime };
}

async function loaded(snapshot: StorySnapshot = storySnapshot(), actions: readonly string[] = ['edit_story'], detailOverrides: Partial<EpisodeDetail> = {}) {
  const state = setup(snapshot, actions, detailOverrides);
  await state.store.load(EPISODE_ID);
  return state;
}

function hintKeys(store: StoryEditorStore): readonly string[] {
  return store.hints().map((hint) => `${hint.messageKey}@${hint.segment ?? 'none'}`);
}

describe('StoryEditorStore structure (10.5.9-T1)', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => { TestBed.resetTestingModule(); vi.useRealTimers(); });

  it('moveTo_KeepsKeywordsAndBeatWithTheParagraph_AndEnablesSave', async () => {
    const snapshot = storySnapshot();
    const { store } = await loaded(snapshot);
    expect(store.saveDisabled()).toBe(true);
    store.updateKeyword(store.paragraphs()[1]?.segKey ?? '', 0, 'changed-on-p1');
    expect(store.moveTo(1, 0)).toBe(true);
    expect(store.paragraphs().map((item) => item.text())).toEqual([paragraph(snapshot, 1), paragraph(snapshot, 0), paragraph(snapshot, 2), paragraph(snapshot, 3)]);
    expect(store.paragraphs()[0]?.keywords()).toEqual(['changed-on-p1', 'mist']);
    expect(store.paragraphs()[0]?.beat).toBe('beat-1');
    expect(store.saveDisabled()).toBe(false);
    expect(store.dirty()).toBe(true);
  });

  it('moveBack_DisablesSave_AndIsNotADirtyDraft', async () => {
    const { store } = await loaded();
    store.moveTo(0, 2);
    expect(store.saveDisabled()).toBe(false);
    store.moveTo(2, 0);
    expect(store.saveDisabled()).toBe(true);
    expect(store.hasUnsavedChanges()).toBe(false);
    expect(store.hints()).toEqual([]);
  });

  it('noOpMove_IsNotDirty_AndDoesNotBumpTheVersion', async () => {
    const { store } = await loaded();
    const version = store.version();
    expect(store.moveTo(1, 1)).toBe(false);
    expect(store.moveUp(0)).toBe(false);
    expect(store.moveDown(3)).toBe(false);
    expect(store.moveTo(9, 0)).toBe(false);
    expect(store.version()).toBe(version);
    expect(store.saveDisabled()).toBe(true);
    expect(store.dirty()).toBe(false);
  });

  it('readOnly_BlocksMovesAndDeletes', async () => {
    const snapshot = storySnapshot();
    const { store } = await loaded(snapshot, []);
    expect(store.readOnly()).toBe(true);
    expect(store.moveTo(0, 1)).toBe(false);
    expect(store.moveUp(1)).toBe(false);
    expect(store.removeParagraph(0)).toBe(false);
    expect(store.canMoveUp(1)).toBe(false);
    expect(store.paragraphs()).toHaveLength(4);
    expect(store.removed()).toBeNull();
  });

  it('dirtyGuard_UsesTheSynchronousCheck_NotTheDebouncedIndicator', async () => {
    const { store } = await loaded();
    store.moveTo(0, 1);
    expect(store.hasUnsavedChanges()).toBe(true);
    expect(store.dirty()).toBe(true);
    expect(store.isDirty()).toBe(false);
    vi.advanceTimersByTime(150);
    expect(store.isDirty()).toBe(true);
  });

  it('deleteP2_HintOfOldP3_StaysOnTheSameText', async () => {
    const { store } = await loaded(storySnapshot({ qualityHints: HINTS }));
    expect(hintKeys(store)).toEqual(['quality.hook_short@p:2', 'quality.length_out_of_range@p:3']);
    expect(store.removeParagraph(2)).toBe(true);
    expect(store.hints()).toEqual([expect.objectContaining({ ruleId: 'length-band', segment: 'p:2', messageKey: 'quality.length_out_of_range' })]);
  });

  it('deleteP2_HintsOfTheDeletedParagraph_AreHiddenWhileBuffered', async () => {
    const { store } = await loaded(storySnapshot({ qualityHints: HINTS }));
    store.removeParagraph(2);
    expect(store.removed()).not.toBeNull();
    expect(hintKeys(store)).toEqual(['quality.length_out_of_range@p:2']);
    expect(store.hints().some((hint) => hint.ruleId === 'hook-short')).toBe(false);
  });

  it('undo_RemapsAddressesBackAndRestoresTheHints', async () => {
    const { store } = await loaded(storySnapshot({ qualityHints: HINTS }));
    store.removeParagraph(2);
    expect(store.undoRemove()).toBe(2);
    expect(store.removed()).toBeNull();
    expect([...hintKeys(store)].sort()).toEqual(['quality.hook_short@p:2', 'quality.length_out_of_range@p:3']);
    expect(store.paragraphs().map((item) => item.text())).toEqual(['Paragraph 1 waits.', 'Paragraph 2 waits.', 'Paragraph 3 waits.', 'Paragraph 4 waits.']);
    expect(store.hasUnsavedChanges()).toBe(false);
  });

  it('move_HintFollowsItsParagraph', async () => {
    const { store } = await loaded(storySnapshot({ qualityHints: HINTS }));
    store.moveTo(3, 0);
    expect(hintKeys(store)).toEqual(['quality.hook_short@p:3', 'quality.length_out_of_range@p:0']);
    store.moveTo(0, 3);
    expect(hintKeys(store)).toEqual(['quality.hook_short@p:2', 'quality.length_out_of_range@p:3']);
  });

  it('deleteThenUndoAfterAnEdit_RestoresTheTextAsRemoved', async () => {
    const { store } = await loaded();
    store.removeParagraph(1);
    store.setSegmentText('title', 'Edited after the delete');
    expect(store.undoRemove()).toBe(1);
    expect(store.paragraphs()[1]?.text()).toBe('Paragraph 2 waits.');
    expect(store.title()).toBe('Edited after the delete');
  });

  it('twoDeletesKeepOneBuffer_AndUndoRestoresTheLastOne', async () => {
    const { store } = await loaded();
    store.removeParagraph(1);
    expect(store.removed()?.index).toBe(1);
    store.removeParagraph(2);
    expect(store.removed()?.index).toBe(2);
    expect(store.paragraphs()).toHaveLength(2);
    expect(store.undoRemove()).toBe(2);
    expect(store.paragraphs().map((item) => item.text())).toEqual(['Paragraph 1 waits.', 'Paragraph 3 waits.', 'Paragraph 4 waits.']);
    expect(store.removed()).toBeNull();
  });

  it('undoAfterTheWindow_ReturnsNull_AndTheDraftStaysDeleted', async () => {
    const { store } = await loaded();
    store.removeParagraph(0);
    vi.advanceTimersByTime(10_000);
    expect(store.removed()).toBeNull();
    expect(store.undoRemove()).toBeNull();
    expect(store.paragraphs()).toHaveLength(3);
  });

  it('aSecondDeleteRestartsTheWindow', async () => {
    const { store } = await loaded();
    store.removeParagraph(0);
    vi.advanceTimersByTime(6_000);
    store.removeParagraph(0);
    vi.advanceTimersByTime(6_000);
    expect(store.removed()).not.toBeNull();
    vi.advanceTimersByTime(4_000);
    expect(store.removed()).toBeNull();
  });

  it('destroy_ClearsTheUndoBuffer', async () => {
    const { store } = await loaded();
    store.removeParagraph(0);
    expect(store.removed()).not.toBeNull();
    TestBed.resetTestingModule();
    expect(store.removed()).toBeNull();
    vi.advanceTimersByTime(10_000);
    expect(store.removed()).toBeNull();
  });

  it('adopt_ClearsTheUndoBuffer', async () => {
    const { store, episodes } = await loaded();
    store.removeParagraph(0);
    episodes.scriptGet(() => Promise.resolve(episodeDetail({ currentStory: storySnapshot(), hasCurrentStory: true, nextActions: ['edit_story'], status: 'story_review' })));
    await store.load(EPISODE_ID);
    expect(store.removed()).toBeNull();
    expect(store.paragraphs()).toHaveLength(4);
  });

  it('save_SendsTheNewOrder_AndTheBaselineFollowsIt', async () => {
    const { store, stories } = await loaded();
    store.moveTo(0, 3);
    expect(store.saveDisabled()).toBe(false);
    await store.saveVersion();
    expect(stories.calls).toHaveLength(1);
    expect(stories.calls[0]?.content.paragraphs.map((item) => item.text)).toEqual(['Paragraph 2 waits.', 'Paragraph 3 waits.', 'Paragraph 4 waits.', 'Paragraph 1 waits.']);
    expect(store.saveDisabled()).toBe(true);
    expect(store.hasUnsavedChanges()).toBe(false);
    expect(store.dirty()).toBe(false);
  });

  it('reorderWhileSaving_LeavesTheDraftDirtyAgain', async () => {
    const { store, stories } = await loaded();
    let resolveSave: ((value: StorySnapshot) => void) | undefined;
    stories.scriptSave(() => new Promise<StorySnapshot>((resolve) => { resolveSave = resolve; }));
    store.moveTo(0, 1);
    const saving = store.saveVersion();
    store.moveTo(1, 0);
    resolveSave?.(storySnapshot());
    await saving;
    expect(store.hasUnsavedChanges()).toBe(true);
    expect(store.dirty()).toBe(true);
    expect(store.saveDisabled()).toBe(false);
  });

  it('discardDraft_ReturnsToTheBaselineSynchronously', async () => {
    const { store } = await loaded();
    store.moveTo(0, 2);
    store.removeParagraph(0);
    store.setSegmentText('title', 'Discarded');
    expect(store.hasUnsavedChanges()).toBe(true);
    store.discardDraft();
    expect(store.hasUnsavedChanges()).toBe(false);
    expect(store.dirty()).toBe(false);
    expect(store.removed()).toBeNull();
    expect(store.title()).toBe('The lantern');
    expect(store.paragraphs().map((item) => item.text())).toEqual(['Paragraph 1 waits.', 'Paragraph 2 waits.', 'Paragraph 3 waits.', 'Paragraph 4 waits.']);
  });

  it('structureWarning_FollowsTheSegmentCountAgainstTheTierBand', async () => {
    // C9 bands count segments (hook + paragraphs + ending), exactly as the backend validator does.
    const two = storySnapshot({ content: storyContent({ paragraphs: storyContent().paragraphs.slice(0, 2) }) });
    const short = await loaded(two, ['edit_story'], { lengthTier: 'short' });
    expect(short.store.structureWarning()).toBe(false);
    short.store.removeParagraph(0);
    expect(short.store.structureWarning()).toBe(true);
    TestBed.resetTestingModule();

    const standard = await loaded(storySnapshot(), ['edit_story'], { lengthTier: 'standard' });
    expect(standard.store.structureWarning()).toBe(true);

    TestBed.resetTestingModule();
    const unknown = await loaded(storySnapshot(), ['edit_story'], { lengthTier: 'epic' });
    expect(unknown.store.structureWarning()).toBe(false);
  });

  it('moveWhileBuffered_UndoRestoresTheHintOnTheRestoredParagraph', async () => {
    const snapshot = storySnapshot({ qualityHints: HINT_ON_P1 });
    const { store } = await loaded(snapshot);
    expect(store.removeParagraph(1)).toBe(true); // [P1, P3, P4]: the p:1 hint waits in the buffer with its paragraph
    expect(store.moveTo(0, 2)).toBe(true); // [P3, P4, P1]: a move reorders the live paragraphs only
    expect(store.undoRemove()).toBe(1); // the buffered paragraph returns at its own index, hint and all
    expect(store.paragraphs().map((item) => item.text())).toEqual([
      paragraph(snapshot, 2), paragraph(snapshot, 1), paragraph(snapshot, 3), paragraph(snapshot, 0),
    ]);
    expect(hintKeys(store)).toEqual(['quality.hook_short@p:1']);
  });

  it('deleteFirstParagraph_UndoRestoresItAtIndexZero', async () => {
    const snapshot = storySnapshot();
    const { store } = await loaded(snapshot);
    store.removeParagraph(0);
    expect(store.paragraphs().map((item) => item.text())).toEqual([paragraph(snapshot, 1), paragraph(snapshot, 2), paragraph(snapshot, 3)]);
    expect(store.undoRemove()).toBe(0);
    expect(store.paragraphs().map((item) => item.text())).toEqual([paragraph(snapshot, 0), paragraph(snapshot, 1), paragraph(snapshot, 2), paragraph(snapshot, 3)]);
    expect(store.hasUnsavedChanges()).toBe(false);
  });

  it('deleteLastParagraph_UndoRestoresItAtTheEnd', async () => {
    const snapshot = storySnapshot();
    const { store } = await loaded(snapshot);
    store.removeParagraph(3);
    expect(store.paragraphs()).toHaveLength(3);
    expect(store.undoRemove()).toBe(3);
    expect(store.paragraphs().map((item) => item.text())).toEqual([paragraph(snapshot, 0), paragraph(snapshot, 1), paragraph(snapshot, 2), paragraph(snapshot, 3)]);
    expect(store.hasUnsavedChanges()).toBe(false);
  });

  it('deleteEveryParagraph_UndoRestoresOnlyTheLastRemovedOne', async () => {
    const snapshot = storySnapshot();
    const { store } = await loaded(snapshot);
    store.removeParagraph(0);
    store.removeParagraph(0);
    store.removeParagraph(0);
    store.removeParagraph(0);
    expect(store.paragraphs()).toHaveLength(0);
    expect(store.removed()?.index).toBe(0);
    expect(store.undoRemove()).toBe(0);
    expect(store.paragraphs().map((item) => item.text())).toEqual([paragraph(snapshot, 3)]);
  });

  it('deleteThenSaveInsideTheWindow_UndoStillRestoresTheText', async () => {
    const snapshot = storySnapshot();
    const { store, stories } = await loaded(snapshot);
    store.removeParagraph(1);
    await store.saveVersion();
    expect(stories.calls[0]?.content.paragraphs.map((item) => item.text)).toEqual([paragraph(snapshot, 0), paragraph(snapshot, 2), paragraph(snapshot, 3)]);
    expect(store.hasUnsavedChanges()).toBe(false);
    // The buffer survives the save; undoing it restores the text, so the draft is dirty again against the new baseline.
    expect(store.removed()).not.toBeNull();
    expect(store.undoRemove()).toBe(1);
    expect(store.paragraphs().map((item) => item.text())).toEqual([paragraph(snapshot, 0), paragraph(snapshot, 1), paragraph(snapshot, 2), paragraph(snapshot, 3)]);
    expect(store.hasUnsavedChanges()).toBe(true);
  });

  it('saveFailureAfterReorder_KeepsTheMovedOrderAndStaysDirty', async () => {
    const snapshot = storySnapshot();
    const { store, stories } = await loaded(snapshot);
    stories.scriptSave(() => Promise.reject(new Error('server 500')));
    store.moveTo(0, 3);
    await store.saveVersion();
    expect(store.paragraphs().map((item) => item.text())).toEqual([paragraph(snapshot, 1), paragraph(snapshot, 2), paragraph(snapshot, 3), paragraph(snapshot, 0)]);
    expect(store.hasUnsavedChanges()).toBe(true);
    expect(store.saveDisabled()).toBe(false);
  });

  it('twoDeletesAtTheSameIndex_UndoRestoresOnlyTheLastRemovedParagraph', async () => {
    const snapshot = storySnapshot();
    const { store } = await loaded(snapshot);
    store.removeParagraph(0); // removes "Paragraph 1 waits."
    store.removeParagraph(0); // removes "Paragraph 2 waits." and replaces the buffer
    expect(store.paragraphs().map((item) => item.text())).toEqual([paragraph(snapshot, 2), paragraph(snapshot, 3)]);
    expect(store.removed()?.index).toBe(0);
    expect(store.undoRemove()).toBe(0);
    // Only the last deletion is restored; the earlier one stays gone.
    expect(store.paragraphs().map((item) => item.text())).toEqual([paragraph(snapshot, 1), paragraph(snapshot, 2), paragraph(snapshot, 3)]);
    expect(store.removed()).toBeNull();
    expect(store.hasUnsavedChanges()).toBe(true);
  });
});
