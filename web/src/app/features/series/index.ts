// Public surface of features/series. `data-access/` (the generated-client adapters) and `testing/` are deliberately
// not re-exported; `provideSeriesDataAccess()` binds both ports to their adapters where the series routes are mounted.
export { SERIES_ROUTES, provideSeriesDataAccess } from './series.routes';
export { CatalogRepository } from './application/catalog.repository';
export type { StyleTemplateCatalogResponse, VoiceCatalogResponse } from './application/catalog.repository';
export { SeriesRepository } from './application/series.repository';
export type { CreatedSeries, SeriesListQuery, SeriesPage, UpdatedSeries } from './application/series.repository';
export { SeriesListStore, SERIES_LIST_PAGE_SIZE } from './application/series-list.store';
export { SeriesDetailStore } from './application/series-detail.store';
export { SeriesEditorStore, DIRTY_DEBOUNCE_MS } from './application/series-editor.store';
export type { ConflictChoice, SeriesMode, SeriesStep, SeriesStepControl, SubmitOutcome } from './application/series-editor.store';
export { createSeriesForm, seriesTitleOf, trimmedNonEmpty } from './application/series-form';
export type { SeriesForm, SeriesFormValue } from './application/series-form';
export { VoiceStepStore } from './application/voice-step.store';
export {
  ASPECTS,
  SERIES_TITLE_MAX,
  TOPICS,
  aspectLabelKey,
  isAspect,
  isTopic,
  isTrueCrime,
  topicLabelKey,
} from './domain/series.model';
export type { Aspect, CreateSeriesInput, Series, SeriesListItem, SeriesPatchInput, Topic } from './domain/series.model';
export { STYLE_CLEARED_BY_TOPIC_KEY, STYLE_INACTIVE_KEY, isStyleName, styleHintKey, styleLabelKey } from './domain/style-template.model';
export type { StyleTemplate } from './domain/style-template.model';
export { isTopicCompatible } from './domain/topic-style-compatibility';
export { VOICE_GENDERS, VOICE_TAGS, isLanguage, isVoiceGender, isVoiceTag } from './domain/voice.model';
export type { Language, Voice, VoiceGender, VoiceTag } from './domain/voice.model';
export { safePreviewUrl } from './domain/preview-url';
export { AudioPreviewButton } from './ui/audio-preview-button';
export { VoicePicker } from './ui/voice-picker';
export { SeriesListPage } from './feature/series-list-page';
export { SeriesEditorPage } from './feature/series-editor-page';
export { SeriesDetailPage } from './feature/series-detail-page';
