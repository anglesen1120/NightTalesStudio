import { safePreviewUrl } from './preview-url';
import { isVoiceGender, isVoiceTag, type Voice, type VoiceGender, type VoiceTag } from './voice.model';

/**
 * Everything `VoicePicker` renders for one voice row, precomputed so the component is pure display (D2):
 * wire values are allow-listed here — unknown tags are dropped (a raw tag would render as a bug,
 * 10.4.2-T2), and the preview URL is re-checked against `safePreviewUrl` so a UI consumer can never
 * attach a rejected URL even if a caller bypassed the repository mapper.
 */
export interface VoiceRowViewModel {
  readonly id: string;
  readonly displayName: string;
  readonly language: Voice['language'];
  /** `series.voice.gender.<male|female|neutral>` when the wire gender is listed, otherwise `null`. */
  readonly genderKey: `series.voice.gender.${VoiceGender}` | null;
  /** Raw wire gender, shown verbatim only when `genderKey` is `null` (04/09 owns the vocabulary). */
  readonly genderRaw: string;
  /** Transloco keys `voices.tag.<tag>`; only allow-listed tags survive. */
  readonly tagKeys: readonly string[];
  readonly preview: AudioPreviewViewModel;
}

/** Immutable preview descriptor handed to `AudioPreviewButton`. */
export interface AudioPreviewViewModel {
  readonly url: string | null;
}

export function toAudioPreviewViewModel(voice: Voice): AudioPreviewViewModel {
  // `previewUrl` arrives already validated by the repository mapper; the second pass makes
  // the guarantee local to the UI contract (never trust a `Voice` built outside the mapper).
  return { url: safePreviewUrl(voice.previewUrl) };
}

export function toVoiceRowViewModel(voice: Voice): VoiceRowViewModel {
  const genderKey = isVoiceGender(voice.gender) ? (`series.voice.gender.${voice.gender}` as const) : null;
  const tagKeys = voice.tags.filter(isVoiceTag).map((tag: VoiceTag) => `voices.tag.${tag}`);
  return {
    id: voice.id,
    displayName: voice.displayName,
    language: voice.language,
    genderKey,
    genderRaw: voice.gender,
    tagKeys,
    preview: toAudioPreviewViewModel(voice),
  };
}

/** The picker consumes this immutable list; built once per voices emission. */
export interface VoicePickerViewModel {
  readonly rows: readonly VoiceRowViewModel[];
}

export function toVoicePickerViewModel(voices: readonly Voice[]): VoicePickerViewModel {
  return { rows: voices.map(toVoiceRowViewModel) };
}
