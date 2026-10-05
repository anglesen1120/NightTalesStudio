// No Angular/rxjs/core imports in *.model.ts (README D2: domain imports only its own files and @shared/util).

/**
 * UI language of a voice. `Language` cannot alias `AppLocale` from `@core/i18n` (lint D2 keeps `domain/` free of
 * `@core`); the values are identical by contract (D10: `vi` + `en`).
 */
export type Language = 'vi' | 'en';

export function isLanguage(value: unknown): value is Language {
  return value === 'vi' || value === 'en';
}

/**
 * The fixed tag vocabulary of the voice catalog (plans/features/04-voice-proxy/README.md L213, 04 CR-11).
 * Every value has a verbatim i18n key `voices.tag.<tag>`; an unknown wire tag is dropped by the mapper, never
 * rendered raw.
 */
export const VOICE_TAGS = [
  'storyteller',
  'natural',
  'news',
  'deep',
  'warm',
  'calm',
  'bright',
  'whisper',
  'northern',
  'central',
  'southern',
] as const;

export type VoiceTag = (typeof VOICE_TAGS)[number];

export function isVoiceTag(value: string): value is VoiceTag {
  return (VOICE_TAGS as readonly string[]).includes(value);
}

/**
 * The gender vocabulary rendered as `series.voice.gender.<value>`. [OPEN] 04/09 do not enumerate the wire
 * vocabulary: a wire value outside this list is kept on `Voice.gender` and the picker renders it raw.
 */
export const VOICE_GENDERS = ['male', 'female', 'neutral'] as const;

export type VoiceGender = (typeof VOICE_GENDERS)[number];

export function isVoiceGender(value: string): value is VoiceGender {
  return (VOICE_GENDERS as readonly string[]).includes(value);
}

/** One catalog voice (GET /api/v1/voices item, C13). `previewUrl` is already sanitised by the repository mapper. */
export interface Voice {
  readonly id: string;
  readonly displayName: string;
  readonly language: Language;
  readonly gender: string;
  readonly tags: readonly VoiceTag[];
  readonly previewUrl: string | null;
}
