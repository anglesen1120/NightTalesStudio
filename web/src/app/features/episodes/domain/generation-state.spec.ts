import { failureKey, isTerminal, stageKey } from './generation-state';

describe('generation-state', () => {
  it.each(['queued', 'writing', 'checking', 'reviewing', 'revising', 'scoring', 'saving', 'done'])('knownStage_%s_MapsToCopy', (stage) => {
    expect(stageKey(stage)).toBe(`${stage === 'queued' || stage === 'done' ? 'episodes' : 'story'}.generation.stage.${stage}`);
  });
  it.each(['future_stage', '', null, undefined])('unknownStage_%s_IsNull', (stage) => {
    expect(stageKey(stage)).toBeNull();
  });
  it.each(['succeeded', 'failed', 'cancelled'])('terminal_%s_IsTerminal', (status) => {
    expect(isTerminal(status)).toBe(true);
  });
  it.each(['queued', 'running', 'unknown', null])('nonterminal_%s_IsNotTerminal', (status) => {
    expect(isTerminal(status)).toBe(false);
  });
  it.each(['story_invalid_output', 'story_generation_failed', 'spend_cap_reached'])('failure_%s_UsesStoryCopy', (code) => {
    expect(failureKey(code)).toBe(`story.generation.error.${code}`);
  });
  it('moderated_UsesBriefModerationCopy', () => {
    expect(failureKey('story_moderated')).toBe('story.generation.moderation.brief');
  });
  it.each([null, undefined, ''])('missingError_%s_HasNoFailure', (code) => {
    expect(failureKey(code)).toBeNull();
  });
  it('otherError_DefersCatalogueFallbackToApplication', () => {
    expect(failureKey('fact_sheet_required')).toBeNull();
    expect(failureKey('future_error')).toBeNull();
    expect(failureKey('story_generation_limit')).toBeNull();
  });
});
