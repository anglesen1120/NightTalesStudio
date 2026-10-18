import { describe, expect, it } from 'vitest';
import type { RenderJob } from '../domain/render.model';
import { EMPTY_JOB, GUARANTEED, SUB_STAGES, clampPercent, isGuaranteedStage, isSubStage, renderTimeline, subStepKeyOf } from './render-stage';

type JobInput = Pick<RenderJob, 'status' | 'stage' | 'percent' | 'errorCode'>;

function input(overrides: Partial<JobInput> = {}): JobInput {
  return { status: 'queued', stage: 'queued', percent: 0, errorCode: null, ...overrides };
}

/** `key:state` per step, in order: the whole shape of one timeline in one value. */
function states(job: JobInput): string[] {
  return renderTimeline(job).map((step) => `${step.key}:${step.state}`);
}

/** Hostile or near-miss strings (QA): none of them is a member of either closed list. */
const POISONED_STAGES = ['<img src=x onerror=1>', 'p:999999999999', 'javascript:alert(1)', 'QUEUED', ' running', 'running ', '🤖', '檢查', 'null', 'undefined'];

describe('render-stage lists', () => {
  it('pins the guaranteed order the timeline renders', () => {
    expect(GUARANTEED).toEqual(['queued', 'running', 'ingesting']);
  });

  it('pins the sub-step order the adapter reports', () => {
    expect(SUB_STAGES).toEqual(['voice', 'subtitles', 'images', 'assembling', 'checking']);
  });

  it('recognises only its own members', () => {
    expect(GUARANTEED.every((stage) => isGuaranteedStage(stage))).toBe(true);
    expect(SUB_STAGES.every((stage) => isSubStage(stage))).toBe(true);
    expect(isGuaranteedStage('assembling')).toBe(false);
    expect(isGuaranteedStage('v2')).toBe(false);
    expect(isSubStage('running')).toBe(false);
    expect(isSubStage('RENDERING')).toBe(false);
  });

  it.each(POISONED_STAGES)('drops the hostile non-member %j from every stage lookup', (value) => {
    expect(isGuaranteedStage(value)).toBe(false);
    expect(isSubStage(value)).toBe(false);
    expect(subStepKeyOf(value)).toBeNull();
  });
});

describe('clampPercent', () => {
  it.each([
    [undefined, 0],
    [null, 0],
    [Number.NaN, 0],
    [Number.POSITIVE_INFINITY, 0],
    [-1, 0],
    [0, 0],
    [42, 42],
    [100, 100],
    [250, 100],
  ])('clamps %s to %s', (value, expected) => {
    expect(clampPercent(value)).toBe(expected);
  });
});

describe('subStepKeyOf', () => {
  it.each([[''], ['queued '], ['RENDERING'], ['v2'], ['queued']])('drops the unknown stage %j', (stage) => {
    expect(subStepKeyOf(stage)).toBeNull();
  });

  it.each(SUB_STAGES)('maps %s to itself', (stage) => {
    expect(subStepKeyOf(stage)).toBe(stage);
  });
});

describe('renderTimeline', () => {
  it('marks queued active and the later stages todo', () => {
    expect(states(input())).toEqual(['queued:active', 'running:todo', 'ingesting:todo']);
  });

  it('keeps queued active for submitted, the queued to running boundary (MarkSubmitted, no adapter stage yet)', () => {
    expect(states(input({ status: 'submitted', stage: 'running' }))).toEqual(['queued:active', 'running:todo', 'ingesting:todo']);
  });

  it('marks queued done and running active while the node works', () => {
    expect(states(input({ status: 'running', stage: 'running', percent: 42 }))).toEqual(['queued:done', 'running:active', 'ingesting:todo']);
  });

  it('carries the sub-step on the active running step and the job percent there', () => {
    const steps = renderTimeline(input({ status: 'running', stage: 'assembling', percent: 42 }));
    expect(steps.map((step) => step.subStepKey)).toEqual([null, 'assembling', null]);
    expect(steps.map((step) => step.percent)).toEqual([100, 42, null]);
  });

  it('marks queued and running done once the job ingests, and shows no sub-step', () => {
    expect(states(input({ status: 'ingesting', stage: 'assembling', percent: 90 }))).toEqual(['queued:done', 'running:done', 'ingesting:active']);
    expect(renderTimeline(input({ status: 'ingesting', stage: 'assembling' })).every((step) => step.subStepKey === null)).toBe(true);
  });

  it('marks every stage done when the job succeeded', () => {
    const steps = renderTimeline(input({ status: 'succeeded', stage: 'ingesting', percent: 100 }));
    expect(steps.map((step) => `${step.key}:${step.state}`)).toEqual(['queued:done', 'running:done', 'ingesting:done']);
    expect(steps.map((step) => step.percent)).toEqual([100, 100, 100]);
  });

  it('marks the reached stage failed and never a later stage done', () => {
    expect(states(input({ status: 'failed', stage: 'assembling', percent: 61, errorCode: 'render_assembly_failed' })))
      .toEqual(['queued:done', 'running:failed', 'ingesting:todo']);
  });

  it('takes a failed job with no usable stage to have reached running', () => {
    expect(states(input({ status: 'failed', stage: '', percent: 0, errorCode: 'render_node_error' })))
      .toEqual(['queued:done', 'running:failed', 'ingesting:todo']);
  });

  it('marks a failed job that reported queued as failed at queued, with nothing done', () => {
    expect(states(input({ status: 'failed', stage: 'queued', percent: 0, errorCode: 'tts_failed' })))
      .toEqual(['queued:failed', 'running:todo', 'ingesting:todo']);
  });

  it('never marks a stage failed for a cancelled job', () => {
    const cancelled = renderTimeline(input({ status: 'cancelled', stage: 'running', percent: 40 }));
    expect(cancelled.map((step) => `${step.key}:${step.state}`)).toEqual(['queued:done', 'running:todo', 'ingesting:todo']);
    expect(cancelled.some((step) => step.state === 'failed')).toBe(false);
  });

  it('reaches nothing when a cancelled job never reported a stage', () => {
    expect(states(input({ status: 'cancelled', stage: '', percent: 0 }))).toEqual(['queued:todo', 'running:todo', 'ingesting:todo']);
  });

  it('renders three todo steps for the no-job placeholder, inventing no stage', () => {
    expect(renderTimeline(EMPTY_JOB)).toEqual([
      { key: 'queued', state: 'todo', percent: null, subStepKey: null },
      { key: 'running', state: 'todo', percent: null, subStepKey: null },
      { key: 'ingesting', state: 'todo', percent: null, subStepKey: null },
    ]);
  });

  it('clamps the job percent into the active step', () => {
    expect(renderTimeline(input({ status: 'running', stage: 'voice', percent: 250 }))[1]?.percent).toBe(100);
    expect(renderTimeline(input({ status: 'running', stage: 'voice', percent: Number.NaN }))[1]?.percent).toBe(0);
  });

  it('keeps the guaranteed order and the closed state set for every status/stage combination', () => {
    const statuses: ReadonlyArray<JobInput['status']> = ['queued', 'submitted', 'running', 'ingesting', 'succeeded', 'failed', 'cancelled'];
    const stages = ['', 'queued', 'running', 'ingesting', ...SUB_STAGES, ...POISONED_STAGES];
    for (const status of statuses) {
      for (const stage of stages) {
        const steps = renderTimeline(input({ status, stage, percent: 42 }));
        expect(steps.map((step) => step.key)).toEqual([...GUARANTEED]);
        for (const step of steps) {
          expect(['done', 'active', 'todo', 'failed']).toContain(step.state);
          // The percent contract: `null` exactly while `todo`, `100` exactly while `done`, a clamped number while active/failed.
          if (step.state === 'todo') expect(step.percent).toBeNull();
          else if (step.state === 'done') expect(step.percent).toBe(100);
          else expect(step.percent).toBe(42);
        }
      }
    }
  });
});
