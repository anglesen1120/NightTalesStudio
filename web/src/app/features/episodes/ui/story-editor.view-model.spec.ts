import { describe, expect, it } from 'vitest';
import { buildStoryEditorViewModel, EMPTY_ERRORS } from './story-editor.view-model';
import type { StoryEditorPresentationState } from './story-editor.view-model';

function state(): StoryEditorPresentationState {
  return {
    title: 'Title', hook: 'Hook', ending: 'Ending', hookKey: 'hook', endingKey: 'ending',
    hookKeywords: ['rain'], endingKeywords: ['sunrise'],
    paragraphs: [
      { segKey: 'p-0', beat: 'setup', text: 'First paragraph', keywords: ['street'] },
      { segKey: 'p-1', beat: 'payoff', text: 'Second paragraph', keywords: ['light'] },
    ],
    meta: { title: 'Title', description: 'Description', tags: [], contentWarnings: [], structure: null },
    metaTokens: 1, segmentTokens: new Map([['title', 1], ['hook', 1], ['p:0', 2], ['p:1', 2], ['ending', 1], ['meta', 1]]), totalTokens: 8,
    fieldErrors: {}, formErrors: [], readOnly: false, keywordLimitHit: null, structureWarning: false,
    isDirty: false, saving: false, savedAt: null, errorKey: null, saveDisabled: true,
  };
}

describe('buildStoryEditorViewModel', () => {
  it('threads the non-blocking structure warning without recreating unchanged paragraph models', () => {
    const initial = state();
    const before = buildStoryEditorViewModel(initial);
    const after = buildStoryEditorViewModel({ ...initial, structureWarning: true, saveDisabled: false }, before);
    expect(before.structureWarning).toBe(false);
    expect(after.structureWarning).toBe(true);
    expect(after.saveBar.disabled).toBe(false);
    expect(after.paragraphs[0]).toBe(before.paragraphs[0]);
    expect(after.paragraphs[1]).toBe(before.paragraphs[1]);
  });

  it('allocates only the edited segment model for a one-character edit', () => {
    const initial = state();
    const before = buildStoryEditorViewModel(initial);
    const after = buildStoryEditorViewModel({ ...initial, paragraphs: [{ ...initial.paragraphs[0]!, text: 'First paragraph!' }, initial.paragraphs[1]!] }, before);
    expect(after.paragraphs[0]).not.toBe(before.paragraphs[0]);
    expect(after.paragraphs[1]).toBe(before.paragraphs[1]);
    expect(after.title).toBe(before.title);
    expect(after.hook).toBe(before.hook);
    expect(after.ending).toBe(before.ending);
    expect(after.title.errorKeys).toBe(EMPTY_ERRORS);
    expect(after.paragraphs[0]!.errorKeys).toBe(EMPTY_ERRORS);
    expect(after.metaTokens).toBe(initial.metaTokens);
  });

  it('reuses equal keyword/error values but updates changed tokens, errors and read-only state', () => {
    const initial = { ...state(), fieldErrors: { hook: ['errors.story_invalid_content'] } };
    const before = buildStoryEditorViewModel(initial);
    const same = buildStoryEditorViewModel({ ...initial, hookKeywords: ['rain'], fieldErrors: { hook: ['errors.story_invalid_content'] } }, before);
    expect(same.hook).toBe(before.hook);
    const tokens = buildStoryEditorViewModel({ ...initial, segmentTokens: new Map(initial.segmentTokens).set('hook', 5) }, same);
    expect(tokens.hook).not.toBe(same.hook);
    expect(tokens.hook.tokens).toBe(5);
    const cleared = buildStoryEditorViewModel({ ...initial, fieldErrors: {} }, before);
    expect(cleared.hook).not.toBe(before.hook);
    expect(cleared.hook.errorKeys).toBe(EMPTY_ERRORS);
    const locked = buildStoryEditorViewModel({ ...initial, readOnly: true }, before);
    expect(locked.hook).not.toBe(before.hook);
    expect(locked.hook.readOnly).toBe(true);
  });

  it('does not reuse a paragraph model after its segment identity or address changes', () => {
    const initial = state();
    const before = buildStoryEditorViewModel(initial);
    const reordered = buildStoryEditorViewModel({ ...initial, paragraphs: [...initial.paragraphs].reverse() }, before);
    expect(reordered.paragraphs[0]).not.toBe(before.paragraphs[0]);
    expect(reordered.paragraphs[0]!.segKey).toBe('p-1');
    expect(reordered.paragraphs[0]!.address).toBe('p:0');
  });

  it('prioritizes saving and mapped errors, and falls through to dirty after a dismissed conflict', () => {
    const initial = state();
    expect(buildStoryEditorViewModel({ ...initial, saving: true, errorKey: 'errors.story_invalid_content' }).saveBar.state).toBe('saving');
    const failed = buildStoryEditorViewModel({ ...initial, isDirty: true, errorKey: 'errors.story_invalid_content' }).saveBar;
    expect(failed.state).toBe('error');
    expect(failed.errorKey).toBe('errors.story_invalid_content');
    expect(buildStoryEditorViewModel({ ...initial, isDirty: true, errorKey: null }).saveBar.state).toBe('dirty');
    expect(buildStoryEditorViewModel({ ...initial, savedAt: 1 }).saveBar.state).toBe('saved');
    expect(buildStoryEditorViewModel(initial).saveBar.state).toBe('clean');
  });
});
