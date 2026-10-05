import { ASPECTS, TOPICS, aspectLabelKey, isAspect, isTopic, isTrueCrime, topicLabelKey, type Topic } from './series.model';
import { isStyleName, styleHintKey, styleLabelKey, type StyleTemplate } from './style-template.model';
import { isTopicCompatible } from './topic-style-compatibility';

/**
 * The full `StyleTopicPolicy` table (03.1-style-system/plan.md:81-82, `src/NightTales.Domain/Catalog/StyleTopicPolicy.cs`):
 * `true_crime × {true_crime ✓, else ✗}`; `ghost|spiritual × {true_crime ✗, else ✓}`, `topicHint === null` allowed for
 * fiction only. Every cell is asserted, so the client mirror and the backend table cannot drift apart unnoticed.
 */
describe('isTopicCompatible (StyleTopicPolicy table)', () => {
  const HINTS = ['ghost', 'spiritual', 'true_crime', null] as const;

  /** Expected cells, written from the backend table rather than from the implementation. */
  const EXPECTED: Readonly<Record<Topic, Readonly<Record<string, boolean>>>> = {
    ghost: { ghost: true, spiritual: true, true_crime: false, null: true },
    spiritual: { ghost: true, spiritual: true, true_crime: false, null: true },
    true_crime: { ghost: false, spiritual: false, true_crime: true, null: false },
  };

  it.each(TOPICS.flatMap((topic) => HINTS.map((hint) => [topic, hint] as const)))(
    'topic %s with topicHint %s matches the policy table',
    (topic, hint) => {
      expect(isTopicCompatible(topic, { topicHint: hint })).toBe(EXPECTED[topic][String(hint)]);
    },
  );

  it('TrueCrime_NeedsATrueCrimeTemplate_NullIsNotEnough', () => {
    expect(isTopicCompatible('true_crime', { topicHint: 'true_crime' })).toBe(true);
    expect(isTopicCompatible('true_crime', { topicHint: null })).toBe(false);
    expect(isTopicCompatible('true_crime', { topicHint: 'ghost' })).toBe(false);
  });

  it('TrueCrimeTemplate_IsRefusedByBothFictionTopics', () => {
    for (const topic of ['ghost', 'spiritual'] as const) {
      expect(isTopicCompatible(topic, { topicHint: 'true_crime' })).toBe(false);
    }
  });

  it('NullHint_OnlyAllowedForFiction', () => {
    expect(isTopicCompatible('ghost', { topicHint: null })).toBe(true);
    expect(isTopicCompatible('spiritual', { topicHint: null })).toBe(true);
    expect(isTopicCompatible('true_crime', { topicHint: null })).toBe(false);
  });

  it('ReadsOnlyTopicHint_IgnoresEveryOtherTemplateMember', () => {
    // A whole `StyleTemplate`, not just the `Pick`: the excess members are the point of the test.
    const template: StyleTemplate = { id: 'x', name: 'case-file-illustration', topicHint: 'ghost', previewUrl: null };
    expect(isTopicCompatible('ghost', template)).toBe(true);
  });
});

describe('series.model (wire vocabulary and keys)', () => {
  it('Topic_GuardsAcceptOnlyTheThreeWireValues', () => {
    expect(TOPICS).toEqual(['ghost', 'spiritual', 'true_crime']);
    expect(TOPICS.every(isTopic)).toBe(true);
    for (const rejected of ['ghost_crime', 'TrueCrime', 'true-crime', '', null, 7, {}]) {
      expect(isTopic(rejected)).toBe(false);
    }
  });

  it('Aspect_GuardsAcceptOnlyTheTwoWireValues', () => {
    expect(ASPECTS).toEqual(['landscape', 'portrait']);
    expect(ASPECTS.every(isAspect)).toBe(true);
    for (const rejected of ['square', 'Landscape', '', null, 1]) {
      expect(isAspect(rejected)).toBe(false);
    }
  });

  it('TopicLabelKey_MapsSnakeWireToCamelLeaf', () => {
    expect(topicLabelKey('ghost')).toBe('series.topic.ghost');
    expect(topicLabelKey('spiritual')).toBe('series.topic.spiritual');
    expect(topicLabelKey('true_crime')).toBe('series.topic.trueCrime');
  });

  it('AspectLabelKey_UsesTheValueVerbatim', () => {
    expect(aspectLabelKey('landscape')).toBe('series.aspect.landscape');
    expect(aspectLabelKey('portrait')).toBe('series.aspect.portrait');
  });

  it('IsTrueCrime_IsTrueForTrueCrimeOnly', () => {
    expect(isTrueCrime('true_crime')).toBe(true);
    expect(isTrueCrime('ghost')).toBe(false);
    expect(isTrueCrime('spiritual')).toBe(false);
  });
});

describe('style-template.model (seed keys)', () => {
  it('StyleKeys_BuildTheSeedKeyShape', () => {
    expect(styleLabelKey('dark-folklore-ink')).toBe('series.style.dark-folklore-ink.label');
    expect(styleHintKey('case-file-illustration')).toBe('series.style.case-file-illustration.hint');
  });

  it('IsStyleName_AcceptsTheSevenSeedSlugs_AndNothingElse', () => {
    const seedNames = [
      'dark-folklore-ink',
      'foggy-cinematic',
      'vintage-photo-grain',
      'watercolor-night',
      'temple-candlelight',
      'noir-sketch',
      'case-file-illustration',
    ];
    expect(seedNames.every(isStyleName)).toBe(true);
    for (const hostile of ['../secret', 'UPPER', 'has space', 'dot.name', '', '-lead', 'trail-', 'a'.repeat(65), 'vi_underscore']) {
      expect(isStyleName(hostile)).toBe(false);
    }
  });
});
