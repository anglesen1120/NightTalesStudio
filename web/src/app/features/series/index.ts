// Public surface of features/series. `data-access/` (the generated-client adapter) and `testing/` are deliberately not
// re-exported; `provideSeriesDataAccess()` binds the port to its adapter where the series routes are mounted.
export { SERIES_ROUTES, provideSeriesDataAccess } from './series.routes';
export { CatalogRepository } from './application/catalog.repository';
export type { VoiceCatalogResponse } from './application/catalog.repository';
export { VoiceStepStore } from './application/voice-step.store';
export { VOICE_GENDERS, VOICE_TAGS, isLanguage, isVoiceGender, isVoiceTag } from './domain/voice.model';
export type { Language, Voice, VoiceGender, VoiceTag } from './domain/voice.model';
export { safePreviewUrl } from './domain/preview-url';
export { AudioPreviewButton } from './ui/audio-preview-button';
export { VoicePicker } from './ui/voice-picker';
