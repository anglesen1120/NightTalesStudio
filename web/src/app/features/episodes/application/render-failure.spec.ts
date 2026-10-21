import { describe, expect, it } from 'vitest';
import { ERROR_CODES } from '@core/errors';
import errorsEn from '../../../../assets/i18n/errors/en.json';
import errorsVi from '../../../../assets/i18n/errors/vi.json';
import episodesEn from '../../../../assets/i18n/episodes/en.json';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import type { RenderJob, SkippedScene } from '../domain/render.model';
import { BODY_ONLY_CODES, GENERIC_TITLE_KEY, JOB_FAILURE_CODES, failureCopy, imageDetailCode } from './render-failure';

const ERRORS_EN = errorsEn as unknown as Readonly<Record<string, string>>;
const ERRORS_VI = errorsVi as unknown as Readonly<Record<string, string>>;
const BODIES_EN = episodesEn.render.failure.code as Readonly<Record<string, string>>;
const BODIES_VI = episodesVi.render.failure.code as Readonly<Record<string, string>>;

/**
 * The F7 table of the task file, frozen here: the codes that own an `errors.<code>` title (README D9's shared error
 * catalogue), the codes whose copy is a `render.failure.code.<code>` body only, and the action each one offers. The
 * rules themselves live in `render-failure.ts` alone; these constants only mirror the table the module must satisfy.
 */
const TITLED_CODES = [
  'tts_failed',
  'image_moderated',
  'image_generation_failed',
  'render_assembly_failed',
  'render_node_error',
  'render_lost',
  'render_timeout',
  'render_output_invalid',
  'render_quality_failed',
] as const;
const BODY_CODES = ['images_incomplete', 'provider_funds_exhausted', 'upstream_error'] as const;
const RETRY_CODES = [
  'tts_failed',
  'render_assembly_failed',
  'render_node_error',
  'render_lost',
  'render_timeout',
  'render_output_invalid',
  'render_quality_failed',
] as const;
const EDIT_SCENES_CODES = ['image_moderated', 'image_generation_failed'] as const;

function job(errorCode: string | null, images: RenderJob['images'] = null): RenderJob {
  return {
    renderJobId: 'job-1',
    status: 'failed',
    stage: 'running',
    percent: 0,
    attempt: 1,
    imagesNeeded: 0,
    submittedAt: null,
    finishedAt: null,
    errorCode,
    hints: [],
    images,
  };
}

function skip(segment: string, reason: string): SkippedScene {
  return { segment, reason };
}

describe('failureCopy', () => {
  it('pins the F7 code lists to the module and to the shared error catalogue', () => {
    expect([...JOB_FAILURE_CODES].sort()).toEqual([...TITLED_CODES].sort());
    expect([...BODY_ONLY_CODES].sort()).toEqual([...BODY_CODES].sort());
    // The nine titles live in hand-synced lists; this fails if a code the module titles is missing from the catalogue.
    expect(JOB_FAILURE_CODES.filter((code) => !Object.hasOwn(ERROR_CODES, code))).toEqual([]);
  });

  it.each(TITLED_CODES)('titles %s from the error catalogue', (code) => {
    expect(failureCopy(code, []).titleKey).toBe(`errors.${code}`);
    expect(failureCopy(code, []).bodyKeys).toEqual([]);
  });

  it.each(BODY_CODES)('explains %s with its body key under the generic title', (code) => {
    expect(failureCopy(code, [])).toEqual({ titleKey: GENERIC_TITLE_KEY, bodyKeys: [`render.failure.code.${code}`], actionKeys: [] });
  });

  it.each(JOB_FAILURE_CODES)('resolves %s to an errors.<code> title that exists in both bundles', (code) => {
    expect(failureCopy(code, []).titleKey).toBe(`errors.${code}`);
    expect(ERRORS_EN[code]).toBeTypeOf('string');
    expect(ERRORS_VI[code]).toBeTypeOf('string');
  });

  it.each(BODY_ONLY_CODES)('resolves %s to a body present in both bundles, with no title of its own', (code) => {
    const copy = failureCopy(code, []);
    expect(copy.titleKey).toBe(GENERIC_TITLE_KEY);
    expect(copy.bodyKeys).toEqual([`render.failure.code.${code}`]);
    expect(BODIES_EN[code]).toBeTypeOf('string');
    expect(BODIES_VI[code]).toBeTypeOf('string');
    // Known decision: these three are not in the shared catalogue, which is why they borrow the generic title.
    expect(Object.hasOwn(ERRORS_EN, code)).toBe(false);
  });

  it.each(RETRY_CODES)('offers a re-make for %s', (code) => {
    expect(failureCopy(code, []).actionKeys).toEqual(['retry']);
  });

  it.each(EDIT_SCENES_CODES)('offers the scene editor for %s when a moderated scene is named', (code) => {
    expect(failureCopy(code, [skip('seg-1', 'moderated')]).actionKeys).toEqual(['editScenes']);
  });

  it('offers nothing for a moderated image with no moderated scene to fix', () => {
    expect(failureCopy('image_moderated', []).actionKeys).toEqual([]);
    expect(failureCopy('image_moderated', [skip('seg-1', 'timeout')]).actionKeys).toEqual([]);
  });

  it('offers nothing for an image failure that only skipped non-moderated scenes', () => {
    expect(failureCopy('image_generation_failed', [skip('seg-1', 'timeout'), skip('seg-2', 'relay_rejected')]).actionKeys).toEqual([]);
  });

  it('lists the moderated scene whatever its address looks like', () => {
    // The address is parsed only by the skipped-scenes link (10.5.3-T1): one that fails `parse` stays a plain row.
    const copy = failureCopy('image_moderated', [skip('not-an-address', 'moderated')]);
    expect(copy.actionKeys).toEqual(['editScenes']);
    expect(copy.titleKey).toBe('errors.image_moderated');
  });

  it.each(['a_code_from_a_later_wave', '', '<img src=x onerror=1>', 'render_quality_failed_2'])(
    'falls back to the generic title for the unknown code %j, and offers nothing',
    (code) => {
      expect(failureCopy(code, [skip('seg-1', 'moderated')]))
        .toEqual({ titleKey: GENERIC_TITLE_KEY, bodyKeys: [], actionKeys: [] });
    },
  );

  it('never titles a cancellation as a failure', () => {
    expect(failureCopy('render_cancelled', [])).toEqual({ titleKey: GENERIC_TITLE_KEY, bodyKeys: [], actionKeys: [] });
  });
});

describe('imageDetailCode', () => {
  it('has nothing to say without images', () => {
    expect(imageDetailCode(job('image_moderated'))).toBeNull();
    expect(imageDetailCode(job('image_moderated', { needed: 3, succeeded: 3, skipped: [] }))).toBeNull();
  });

  it('takes the first skip that is not a moderation block', () => {
    const images = { needed: 3, succeeded: 1, skipped: [skip('seg-1', 'moderated'), skip('seg-2', 'timeout'), skip('seg-3', 'relay_rejected')] };
    expect(imageDetailCode(job('image_generation_failed', images))).toBe('timeout');
  });

  it('has nothing to say when every skip is a moderation block', () => {
    const images = { needed: 2, succeeded: 0, skipped: [skip('seg-1', 'moderated'), skip('seg-2', 'moderated')] };
    expect(imageDetailCode(job('image_moderated', images))).toBeNull();
  });

  it('has nothing to say without a job', () => {
    expect(imageDetailCode(null)).toBeNull();
  });
});
