import { mapKeywordsFlagged } from './keyword-flags.mapper';

describe('keyword flags mapper', () => {
  it('mapsValidRowsToCanonicalAddresses', () => {
    expect(mapKeywordsFlagged([
      { segment: 'p:1', index: 2, rule: 'keyword_invalid' },
      { segment: 'hook', index: 0, rule: 'no_scenes' },
      { segment: 'ending', index: 1, rule: 'keyword_name_detected' },
    ])).toEqual([
      { address: 'p:1', index: 2, rule: 'keyword_invalid' },
      { address: 'hook', index: 0, rule: 'no_scenes' },
      { address: 'ending', index: 1, rule: 'keyword_name_detected' },
    ]);
  });

  it('ignoresRowsThatAreNotObjects', () => {
    expect(mapKeywordsFlagged([null, 'p:0', 42, [], undefined, { segment: 'p:0', index: 0, rule: 'x' }]))
      .toEqual([{ address: 'p:0', index: 0, rule: 'x' }]);
  });

  it('ignoresAnUnknownOrNonCanonicalSegmentAddress', () => {
    expect(mapKeywordsFlagged([
      { segment: 'unknown', index: 0, rule: 'x' },
      { segment: 'p:01', index: 0, rule: 'x' },
      { segment: 'paragraphs[0]', index: 0, rule: 'x' },
      { segment: 7, index: 0, rule: 'x' },
      { index: 0, rule: 'x' },
    ])).toEqual([]);
  });

  it('ignoresAMissingOrOutOfRangeKeywordIndex', () => {
    expect(mapKeywordsFlagged([
      { segment: 'p:0', rule: 'x' },
      { segment: 'p:0', index: '0', rule: 'x' },
      { segment: 'p:0', index: 1.5, rule: 'x' },
      { segment: 'p:0', index: -1, rule: 'x' },
      { segment: 'p:0', index: 10, rule: 'x' },
      { segment: 'p:0', index: 9, rule: 'x' },
    ])).toEqual([{ address: 'p:0', index: 9, rule: 'x' }]);
  });

  it('dropsARuleThatFailsTheShapeGuard', () => {
    expect(mapKeywordsFlagged([
      { segment: 'p:0', index: 0, rule: 'bad rule' },
      { segment: 'p:0', index: 0, rule: '' },
      { segment: 'p:0', index: 0, rule: 'a'.repeat(65) },
      { segment: 'p:0', index: 0 },
      { segment: 'p:0', index: 0, rule: 12 },
    ])).toEqual([]);
  });

  it('keepsAnUnknownButWellShapedRule', () => {
    expect(mapKeywordsFlagged([{ segment: 'p:0', index: 0, rule: 'brand_new_rule' }]))
      .toEqual([{ address: 'p:0', index: 0, rule: 'brand_new_rule' }]);
  });

  it('dedupesIdenticalRows', () => {
    expect(mapKeywordsFlagged([
      { segment: 'p:1', index: 1, rule: 'keyword_invalid' },
      { segment: 'p:1', index: 1, rule: 'keyword_invalid' },
      { segment: 'p:1', index: 2, rule: 'keyword_invalid' },
    ])).toEqual([
      { address: 'p:1', index: 1, rule: 'keyword_invalid' },
      { address: 'p:1', index: 2, rule: 'keyword_invalid' },
    ]);
  });

  it('capsTheListAt100Rows', () => {
    const rows = Array.from({ length: 150 }, (_, index) => ({ segment: `p:${String(index)}`, index: 0, rule: 'keyword_invalid' }));
    const flags = mapKeywordsFlagged(rows);
    expect(flags).toHaveLength(100);
    expect(flags[99]?.address).toBe('p:99');
  });

  it('returnsNoFlagsForANonArrayPayload', () => {
    for (const payload of [null, undefined, 'p:0', 5, {}, { errors: [] }]) {
      expect(mapKeywordsFlagged(payload)).toEqual([]);
    }
  });
});
