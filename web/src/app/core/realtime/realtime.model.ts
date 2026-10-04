// The realtime vocabulary (README D8): no Angular, no rxjs, no SignalR.

/** `idle` before the first start and after a session end; `stopped` after a bare `stop()`; `paused` is the D7 idle gate. */
export type RealtimeState = 'idle' | 'connecting' | 'connected' | 'backoff' | 'paused' | 'stopped';

/** The canonical SignalR client method names (00 §8, PascalCase): the internal event names. */
export type RealtimeEventName = 'EpisodeUpdated' | 'RenderProgress' | 'PublicationUpdated' | 'CreditBalanceChanged';

/**
 * Every spelling of an event the client accepts, mapped to its internal name: the canonical method names plus the
 * snake_case NOTIFY `event` values (`render_progress`, WR17a) a forwarded raw payload may carry. A name that is not a
 * key is dropped before its payload is read. The one place a wire rename is absorbed.
 */
export const WIRE_TO_NAME: ReadonlyMap<string, RealtimeEventName> = new Map<string, RealtimeEventName>([
  ['EpisodeUpdated', 'EpisodeUpdated'],
  ['RenderProgress', 'RenderProgress'],
  ['PublicationUpdated', 'PublicationUpdated'],
  ['CreditBalanceChanged', 'CreditBalanceChanged'],
  ['episode_updated', 'EpisodeUpdated'],
  ['render_progress', 'RenderProgress'],
  ['publication_updated', 'PublicationUpdated'],
  ['credit_balance_changed', 'CreditBalanceChanged'],
]);

/** The SignalR client method each internal name is registered under on the socket. */
export const NAME_TO_WIRE: Readonly<Record<RealtimeEventName, string>> = {
  EpisodeUpdated: 'EpisodeUpdated',
  RenderProgress: 'RenderProgress',
  PublicationUpdated: 'PublicationUpdated',
  CreditBalanceChanged: 'CreditBalanceChanged',
};

/** Ids only (BD11/BD16): a member that does not apply to the event is absent. */
export interface RealtimeIds {
  readonly seriesId?: string;
  readonly episodeId?: string;
  readonly renderJobId?: string;
  readonly publicationId?: string;
  readonly storyVersionId?: string;
}

/** The render stages of 00 §8 / 05 CR-2; an unknown stage string is dropped. */
export const RENDER_STAGES = [
  'queued',
  'running',
  'ingesting',
  'voice',
  'subtitles',
  'images',
  'assembling',
  'checking',
] as const;
export type RenderStage = (typeof RENDER_STAGES)[number];

/** The one ruled `data` payload (WT54): a stage and a percentage clamped to 0…100. */
export interface RenderProgress {
  readonly stage: RenderStage;
  readonly percent: number;
}

export interface EpisodeUpdated {
  readonly name: 'EpisodeUpdated';
  readonly ids: RealtimeIds;
}
export interface RenderProgressEvent {
  readonly name: 'RenderProgress';
  readonly ids: RealtimeIds;
  readonly progress: RenderProgress;
}
export interface PublicationUpdated {
  readonly name: 'PublicationUpdated';
  readonly ids: RealtimeIds;
}
export interface CreditBalanceChanged {
  readonly name: 'CreditBalanceChanged';
  readonly ids: RealtimeIds;
}

export type RealtimeEvent = EpisodeUpdated | RenderProgressEvent | PublicationUpdated | CreditBalanceChanged;
export type RealtimeEventOf<E extends RealtimeEventName> = Extract<RealtimeEvent, { readonly name: E }>;

/** A single-use hub ticket. Held in a local variable only: never stored, never logged. */
export interface HubTicket {
  readonly ticket: string;
  /** Epoch ms; `0` when the server value is unparsable. */
  readonly expiresAt: number;
}
