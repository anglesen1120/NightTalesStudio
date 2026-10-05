import { VOICE_GENDERS, VOICE_TAGS, isLanguage, isVoiceGender, isVoiceTag, type VoiceTag } from './voice.model';

// The 04 vocabulary (plans/features/04-voice-proxy/README.md L213). Typed as VoiceTag[]: a value dropped from or
// added to the union breaks the build before it breaks this test.
const EXPECTED: readonly VoiceTag[] = ['storyteller', 'natural', 'news', 'deep', 'warm', 'calm', 'bright', 'whisper', 'northern', 'central', 'southern'];

describe('voice.model', () => {
  it('VoiceTag_UnionEqualsTheFixedVocabulary', () => {
    expect([...VOICE_TAGS]).toEqual([...EXPECTED]);
    expect(new Set(VOICE_TAGS).size).toBe(11);
  });

  it('IsVoiceTag_AcceptsOnlyTheVocabulary', () => {
    for (const tag of EXPECTED) expect(isVoiceTag(tag)).toBe(true);
    for (const bad of ['', 'Storyteller', 'robotic', '__proto__', 'constructor']) expect(isVoiceTag(bad)).toBe(false);
  });

  it('IsVoiceGender_AcceptsOnlyTheListedValues', () => {
    expect([...VOICE_GENDERS]).toEqual(['male', 'female', 'neutral']);
    expect(isVoiceGender('female')).toBe(true);
    expect(isVoiceGender('other')).toBe(false);
  });

  it('IsLanguage_AcceptsViAndEnOnly', () => {
    expect(isLanguage('vi')).toBe(true);
    expect(isLanguage('en')).toBe(true);
    expect(isLanguage('fr')).toBe(false);
    expect(isLanguage(null)).toBe(false);
  });
});
