import { EPISODE_STATUSES, LIST_STATUSES, isListStatus, isUuid } from './episode.model';

describe('episode.model', () => {
  it('isListStatus_AcceptsEveryOfferedStatus', () => {
    for (const status of LIST_STATUSES) expect(isListStatus(status)).toBe(true);
    expect(LIST_STATUSES).toHaveLength(EPISODE_STATUSES.length - 1);
  });

  it.each(['cancelled', 'DRAFT', ' draft', 'draft,ready', '__proto__', 'constructor', '', 'unknown', 7, null, undefined, {}, ['draft']])(
    'isListStatus_Rejects_%s',
    (value) => {
      expect(isListStatus(value)).toBe(false);
    },
  );

  it('isUuid_AcceptsCanonicalIdsInAnyCase', () => {
    expect(isUuid('22222222-2222-4222-8222-222222222222')).toBe(true);
    expect(isUuid('ABCDEF12-ABCD-4BCD-8BCD-ABCDEF123456')).toBe(true);
  });

  it.each([
    '',
    'not-a-uuid',
    '22222222-2222-4222-8222-22222222222',
    '22222222-2222-4222-8222-2222222222222',
    '22222222222242228222222222222222',
    '22222222-2222-4222-8222-22222222222g',
    '22222222-2222-4222-8222-222222222222\n',
    '<script>',
    '../series',
    1,
    null,
    undefined,
  ])('isUuid_Rejects_%s', (value) => {
    expect(isUuid(value)).toBe(false);
  });
});
