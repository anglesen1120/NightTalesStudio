import { describe, expect, it, vi } from 'vitest';
import type { InlineAnnotationViewModel } from '@shared/ui';
import type { SegmentAnnotation } from '../domain/annotation.model';
import { buildAnnotationViews, EMPTY_ANNOTATION_VIEWS } from './annotation.view-model';

const hint: SegmentAnnotation = {
  id: 'ann-p-0-Q-LEN-01', source: 'hint', address: 'p:0', ruleId: 'Q-LEN-01', severity: 'blocking', kind: 'objective',
  messageKey: 'quality.passage_long', evidence: 'Saved passage', repairDirection: null, keywordIndex: null, placement: 'inline',
};
const known = { hasKey: () => true };
describe('buildAnnotationViews', () => {
  it('keeps equal output for unchanged annotations and reflects updated evidence', () => {
    const keyword: SegmentAnnotation = { ...hint, id: 'ann-hook-invalid-k0', source: 'keyword', address: 'hook', keywordIndex: 0, messageKey: 'episodes.editor.keywordFlaggedHint' };
    const first = buildAnnotationViews([hint, keyword], known);
    const next = buildAnnotationViews([{ ...hint }, { ...keyword }], known, first);
    expect(next).toEqual(first);
    const changed = buildAnnotationViews([{ ...hint, evidence: 'Changed saved evidence' }, keyword], known, next);
    expect(changed.inline.get('p:0')?.[0]?.evidence).toBe('Changed saved evidence');
    expect(changed.flaggedChips).toEqual(next.flaggedChips);
  });

  it('unknownMessageKey_FallsBackWithoutMirroringKeys', () => {
    const warnUnknown = vi.fn<(key: string) => void>();
    const options = { hasKey: () => false, warnUnknown };
    const missing = { ...hint, messageKey: 'quality.unknown_key' };
    const unsafe = { ...hint, id: 'ann-hook-unsafe', address: 'hook' as const, messageKey: '<script>raw</script>' };
    // Two annotations carry the *same* unknown key: the warning is per distinct key, never per annotation.
    const sharedA = { ...hint, id: 'ann-p-0-shared-a', ruleId: 'R-A', messageKey: 'quality.shared_key' };
    const sharedB = { ...hint, id: 'ann-p-0-shared-b', ruleId: 'R-B', messageKey: 'quality.shared_key' };
    const input = [missing, unsafe, sharedA, sharedB];
    const first = buildAnnotationViews(input, options);
    const next = buildAnnotationViews(input, options, first);
    expect(warnUnknown).toHaveBeenCalledTimes(6);
    expect(warnUnknown.mock.calls.map(([key]) => key)).toEqual([
      'quality.unknown_key', '<script>raw</script>', 'quality.shared_key',
      'quality.unknown_key', '<script>raw</script>', 'quality.shared_key',
    ]);
    expect(next.inline.get('p:0')?.[0]?.messageKey).toBe('episodes.annotations.unknown');
    expect(next.inline.get('hook')?.[0]?.messageKey).toBe('episodes.annotations.unknown');
    // Nothing the view model hands a template carries the server's raw key: not the copy, not the id, not a label.
    const fields = (item: InlineAnnotationViewModel): string =>
      [item.id, item.ruleId, item.messageKey, item.evidence, item.repairDirection ?? '', item.addressLabelKey, item.address ?? ''].join('|');
    const rendered = [
      ...[...next.inline.values()].flat().map(fields),
      ...next.panel.groups.flatMap((group) => group.items).map(fields),
      ...[...next.flaggedChips.values()].flatMap((chips) => [...chips.values()].map((chip) => `${chip.annotationId}|${chip.messageKey}`)),
    ].join('|');
    for (const key of ['quality.unknown_key', '<script>raw</script>', 'quality.shared_key']) expect(rendered).not.toContain(key);
    expect(rendered).toContain('episodes.annotations.unknown');
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
    expect(result.inline.get('p:0')?.[0]?.canGoTo).toBe(false);
    expect(result.panel.groups[0]?.items[0]?.canGoTo).toBe(true);
  });

  it('invalid addresses cannot produce an inline slot or a focus action', () => {
    const result = buildAnnotationViews([{ ...hint, address: 'p:-1' }], known);
    expect(result.inline.size).toBe(0);
    expect(result.panel.groups[0]?.items[0]).toMatchObject({ address: null, canGoTo: false, addressLabelKey: 'episodes.annotations.unresolved' });
  });

  it('uses the shared empty value after all feedback clears', () => {
    expect(buildAnnotationViews([], known, buildAnnotationViews([hint], known))).toEqual(EMPTY_ANNOTATION_VIEWS);
  });
});
