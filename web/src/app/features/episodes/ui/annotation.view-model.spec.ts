import { describe, expect, it, vi } from 'vitest';
import type { SegmentAnnotation } from '../domain/annotation.model';
import { buildAnnotationViews, EMPTY_ANNOTATION_VIEWS } from './annotation.view-model';

const hint: SegmentAnnotation = {
  id: 'ann-p-0-Q-LEN-01', source: 'hint', address: 'p:0', ruleId: 'Q-LEN-01', severity: 'blocking', kind: 'objective',
  messageKey: 'quality.passage_long', evidence: 'Saved passage', repairDirection: null, keywordIndex: null, placement: 'inline',
};
const known = { hasKey: () => true };
describe('buildAnnotationViews', () => {
  it('reuses unchanged address arrays, flags and panel even when incoming objects are recreated', () => {
    const keyword: SegmentAnnotation = { ...hint, id: 'ann-hook-invalid-k0', source: 'keyword', address: 'hook', keywordIndex: 0, messageKey: 'episodes.editor.keywordFlaggedHint' };
    const first = buildAnnotationViews([hint, keyword], known);
    const next = buildAnnotationViews([{ ...hint }, { ...keyword }], known, first);
    expect(next.inline.get('p:0')).toBe(first.inline.get('p:0'));
    expect(next.flaggedChips.get('hook')).toBe(first.flaggedChips.get('hook'));
    expect(next.panel).toBe(first.panel);
    const changed = buildAnnotationViews([{ ...hint, evidence: 'Changed saved evidence' }, keyword], known, next);
    expect(changed.inline.get('p:0')).not.toBe(next.inline.get('p:0'));
    expect(changed.flaggedChips.get('hook')).toBe(next.flaggedChips.get('hook'));
  });

  it('falls back on a missing/unsafe message key without displaying it and warns once per distinct key', () => {
    const warnUnknown = vi.fn();
    const options = { hasKey: () => false, warnUnknown };
    const missing = { ...hint, messageKey: 'quality.sliceC_unknown_key' };
    const unsafe = { ...hint, id: 'ann-hook-unsafe', address: 'hook' as const, messageKey: '<script>raw</script>' };
    const first = buildAnnotationViews([missing, unsafe], options);
    const next = buildAnnotationViews([missing, unsafe], options, first);
    expect(warnUnknown).toHaveBeenCalledTimes(2);
    expect(next.inline.get('p:0')?.[0]?.messageKey).toBe('episodes.annotations.unknown');
    expect(next.inline.get('hook')?.[0]?.messageKey).toBe('episodes.annotations.unknown');
    expect(next.inline.get('p:0')).toBe(first.inline.get('p:0'));
  });

  it('keeps meta and unresolved feedback in the panel and keyword flags on the exact chip', () => {
    const meta = { ...hint, id: 'ann-meta-scene', address: 'meta' as const, placement: 'panel' as const };
    const unresolved = { ...hint, id: 'ann-unresolved-rule', address: null, placement: 'panel' as const, kind: 'taste' as const };
    const keyword = { ...hint, id: 'ann-p-0-invalid-k1', source: 'keyword' as const, keywordIndex: 1, messageKey: 'errors.keywordsFlaggedRule.keyword_invalid' };
    const result = buildAnnotationViews([hint, keyword, meta, unresolved], known);
    expect(result.panel.count).toBe(4);
    expect(result.panel.groups.map((group) => group.kind)).toEqual(['objective', 'taste']);
    expect(result.inline.get('p:0')).toHaveLength(1);
    expect(result.inline.has('meta')).toBe(false);
    expect(result.flaggedChips.get('p:0')?.get(1)).toEqual({ annotationId: keyword.id, messageKey: keyword.messageKey });
    expect(result.panel.groups[0]?.items[2]?.addressLabelKey).toBe('episodes.annotations.meta');
    expect(result.panel.groups[1]?.items[0]).toMatchObject({ addressLabelKey: 'episodes.annotations.unresolved', canGoTo: false });
    expect(result.inline.get('p:0')?.[0]).toMatchObject({ addressLabelKey: 'episodes.editor.segment.paragraph', addressLabelIndex: 1 });
  });

  it('invalid addresses cannot produce an inline slot or a focus action', () => {
    const result = buildAnnotationViews([{ ...hint, address: 'p:-1' }], known);
    expect(result.inline.size).toBe(0);
    expect(result.panel.groups[0]?.items[0]).toMatchObject({ address: null, canGoTo: false, addressLabelKey: 'episodes.annotations.unresolved' });
  });

  it('uses the shared empty value after all feedback clears', () => {
    expect(buildAnnotationViews([], known, buildAnnotationViews([hint], known))).toBe(EMPTY_ANNOTATION_VIEWS);
  });
});
