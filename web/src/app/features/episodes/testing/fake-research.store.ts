import { computed, signal } from '@angular/core';
import type { Provider, Signal } from '@angular/core';
import { EPISODE_GATE } from '../application/episode-gate';
import type { GateState } from '../domain/episode-gate';
import { ResearchStore } from '../application/research.store';

/**
 * The editor-side stand-in for the component-owned research store: the story page reads its gate and one `load()`.
 * Every spec that replaces `StoryReviewPage.providers` adds this pair, so it never reaches a real research store
 * or HTTP client in a unit test.
 */
export interface FakeResearchStore {
  readonly gate: Signal<GateState>;
  readonly formRateLimitSeconds: Signal<number | null>;
  readonly rateLimitSeconds: Signal<number | null>;
  readonly conflictSource: Signal<'save' | 'approve' | 'edit' | null>;
  readonly loadedEpisodeIds: string[];
  load: (episodeId: string) => Promise<{ ok: true; value: undefined }>;
  watch: () => void;
  setGate: (state: GateState) => void;
}

export function fakeResearchStore(initial: GateState = 'open'): FakeResearchStore {
  const gate = signal<GateState>(initial);
  const loadedEpisodeIds: string[] = [];
  return {
    gate: computed(() => gate()),
    formRateLimitSeconds: signal<number | null>(null).asReadonly(),
    rateLimitSeconds: signal<number | null>(null).asReadonly(),
    conflictSource: signal<'save' | 'approve' | 'edit' | null>(null).asReadonly(),
    loadedEpisodeIds,
    load: (episodeId: string) => {
      loadedEpisodeIds.push(episodeId);
      return Promise.resolve({ ok: true, value: undefined });
    },
    watch: () => undefined,
    setGate: (state: GateState) => gate.set(state),
  };
}

/** The provider pair a spec adds to reach {@link fakeResearchStore}: the store token and the gate token it backs. */
export function fakeResearchStoreProviders(store: FakeResearchStore): readonly Provider[] {
  return [
    { provide: ResearchStore, useValue: store },
    { provide: EPISODE_GATE, useValue: store.gate },
  ];
}
