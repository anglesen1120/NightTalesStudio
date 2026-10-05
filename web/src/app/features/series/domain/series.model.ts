// No Angular/rxjs/core imports in *.model.ts (README D2: domain imports only its own files and @shared/util).
import type { Language } from './voice.model';

/**
 * Narrative topic of a series (09.4.1 §3). The wire spelling is the plan's: `true_crime` (snake) while the i18n
 * leaf is `trueCrime` (`topicLabelKey` is the one place that maps one to the other).
 */
export type Topic = 'ghost' | 'spiritual' | 'true_crime';

/** Render order of the topic radio group (03 CR-7 deck, frame `f-new-step1`). */
export const TOPICS = ['ghost', 'spiritual', 'true_crime'] as const satisfies readonly Topic[];

export function isTopic(value: unknown): value is Topic {
  return value === 'ghost' || value === 'spiritual' || value === 'true_crime';
}

/** Video aspect of every episode of the series. */
export type Aspect = 'landscape' | 'portrait';

export const ASPECTS = ['landscape', 'portrait'] as const satisfies readonly Aspect[];

export function isAspect(value: unknown): value is Aspect {
  return value === 'landscape' || value === 'portrait';
}

/** The server's title bound (09.4.1 §5): 1–120 characters, trimmed. */
export const SERIES_TITLE_MAX = 120;

/**
 * One row of `GET /api/v1/series` (09.4.1 §5). `storyPreset` is deliberately absent: the list endpoint does not
 * return it and no list screen reads it (the column exists only on the entity). The per-row `etag` the wire also
 * carries is not modelled here either — archiving is an unconditional `DELETE`, so no list screen ever sends it.
 */
export interface SeriesListItem {
  readonly id: string;
  readonly title: string;
  readonly topic: Topic;
  readonly language: Language;
  readonly aspect: Aspect;
  readonly voiceId: string;
  readonly styleTemplateId: string;
  readonly autopilotEnabled: boolean;
  readonly archivedAt: string | null;
  readonly createdAt: string;
  readonly updatedAt: string;
}

/** One series as `GET /api/v1/series/{id}` returns it, plus the `etag` the next `If-Match` must carry. */
export interface Series extends SeriesListItem {
  readonly storyPreset: string | null;
  readonly etag: string;
}

/** The body of `POST /api/v1/series` (09.4.1 §5). `storyPreset` is `null` in P (no preset picker ships). */
export interface CreateSeriesInput {
  readonly title: string;
  readonly topic: Topic;
  readonly language: Language;
  readonly aspect: Aspect;
  readonly voiceId: string;
  readonly styleTemplateId: string;
  readonly storyPreset: string | null;
}

/**
 * The members `PATCH /api/v1/series/{id}` may change. Every member is optional: the wire contract is a JSON-merge
 * patch where an absent member means "keep the stored value" (09.4.1 §5), so the store sends only what changed.
 * `language` is not part of this type — it is immutable and sending it is a documented 400 (09.4.1 §3).
 */
export interface SeriesPatchInput {
  readonly title?: string;
  readonly topic?: Topic;
  readonly aspect?: Aspect;
  readonly voiceId?: string;
  readonly styleTemplateId?: string;
  readonly storyPreset?: string | null;
}

/** The `series.topic.*` i18n leaf of a topic; the only place the snake wire value becomes a camel leaf. */
export function topicLabelKey(topic: Topic): string {
  return topic === 'true_crime' ? 'series.topic.trueCrime' : `series.topic.${topic}`;
}

/** True for the one topic that restricts the style catalog (03 README D2, `StyleTopicPolicy`). */
export function isTrueCrime(topic: Topic): boolean {
  return topic === 'true_crime';
}

/** The `series.aspect.*` i18n leaf of an aspect. */
export function aspectLabelKey(aspect: Aspect): string {
  return `series.aspect.${aspect}`;
}
