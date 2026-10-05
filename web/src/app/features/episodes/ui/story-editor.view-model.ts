import type { Signal } from '@angular/core';
import type { SegmentAddress } from '@shared/util/segment-address';
import { addressForParagraph } from '@shared/util/segment-address';
import type { StoryMetadata } from '../domain/story.model';

export const EMPTY_ERRORS: readonly string[] = [];
const EMPTY_KEYWORDS: readonly string[] = [];
export type SegmentTexts = ReadonlyMap<SegmentAddress, Signal<string>>;

export interface SegmentEditorViewModel {
  readonly address: SegmentAddress; readonly segKey: string; readonly labelKey: string;
  readonly labelIndex: number | null; readonly beat: string | null; readonly text: string;
  readonly keywords: readonly string[]; readonly tokens: number; readonly errorKeys: readonly string[];
  readonly readOnly: boolean; readonly describedById: string;
}
export type SegmentNavViewModel = Pick<SegmentEditorViewModel, 'address' | 'segKey' | 'labelKey' | 'labelIndex'>;
export type SceneSegmentViewModel = SegmentNavViewModel & Pick<SegmentEditorViewModel, 'keywords'>;
export interface StorySaveBarViewModel {
  readonly state: 'clean' | 'dirty' | 'saving' | 'saved' | 'error'; readonly disabled: boolean;
  readonly errorKey: string | null;
}
export interface StoryEditorViewModel {
  readonly title: SegmentEditorViewModel; readonly hook: SegmentEditorViewModel;
  readonly paragraphs: readonly SegmentEditorViewModel[]; readonly ending: SegmentEditorViewModel;
  readonly meta: StoryMetadata; readonly metaTokens: number; readonly totalTokens: number; readonly readOnly: boolean;
  readonly fieldErrors: Readonly<Partial<Record<SegmentAddress, readonly string[]>>>;
  readonly formErrors: readonly string[]; readonly keywordLimitHit: string | null;
  readonly saveBar: StorySaveBarViewModel;
}
export type StoryEditorIntent =
  | { readonly kind: 'editText'; readonly segKey: string; readonly text: string }
  | { readonly kind: 'editKeyword'; readonly segKey: string; readonly op: 'add' | 'update' | 'remove'; readonly index?: number; readonly value?: string }
  | { readonly kind: 'save' }
  | { readonly kind: 'openSegment'; readonly address: SegmentAddress }
  | { readonly kind: 'resolveConflict'; readonly choice: 'reload' | 'saveAsNew' | 'cancel' };
export interface StoryEditorPresentationState {
  readonly title: string; readonly hook: string; readonly ending: string;
  readonly hookKey: string; readonly endingKey: string; readonly hookKeywords: readonly string[]; readonly endingKeywords: readonly string[];
  readonly paragraphs: readonly { readonly segKey: string; readonly beat: string; readonly text: string; readonly keywords: readonly string[] }[];
  readonly meta: StoryMetadata; readonly metaTokens: number; readonly segmentTokens: ReadonlyMap<SegmentAddress, number>; readonly totalTokens: number;
  readonly fieldErrors: Readonly<Partial<Record<SegmentAddress, readonly string[]>>>; readonly formErrors: readonly string[];
  readonly readOnly: boolean; readonly keywordLimitHit: string | null;
  readonly isDirty: boolean; readonly saving: boolean; readonly savedAt: number | null; readonly errorKey: string | null; readonly saveDisabled: boolean;
}

export function equalStrings(left: readonly string[], right: readonly string[]): boolean {
  return left === right || (left.length === right.length && left.every((value, index) => value === right[index]));
}

export function buildStoryEditorViewModel(state: StoryEditorPresentationState, previous?: StoryEditorViewModel): StoryEditorViewModel {
  const segment = (address: SegmentAddress, segKey: string, text: string, keywords: readonly string[], beat: string | null, labelIndex: number | null, old?: SegmentEditorViewModel): SegmentEditorViewModel => {
    const tokens = state.segmentTokens.get(address) ?? 0;
    const errorKeys = state.fieldErrors[address] ?? EMPTY_ERRORS;
    if (old !== undefined && old.address === address && old.segKey === segKey && old.text === text && old.beat === beat &&
      old.labelIndex === labelIndex && old.tokens === tokens && old.readOnly === state.readOnly &&
      equalStrings(old.keywords, keywords) && equalStrings(old.errorKeys, errorKeys)) return old;
    return {
      address, segKey, text, keywords, beat, labelIndex,
      labelKey: `episodes.editor.segment.${labelIndex === null ? address : 'paragraph'}`,
      tokens, errorKeys, readOnly: state.readOnly, describedById: `seg-${address.replace(':', '-')}-feedback`,
    };
  };
  return {
    title: segment('title', 'title', state.title, EMPTY_KEYWORDS, null, null, previous?.title),
    hook: segment('hook', state.hookKey, state.hook, state.hookKeywords, null, null, previous?.hook),
    paragraphs: state.paragraphs.map((paragraph, index) => segment(addressForParagraph(index), paragraph.segKey, paragraph.text, paragraph.keywords, paragraph.beat, index + 1, previous?.paragraphs[index])),
    ending: segment('ending', state.endingKey, state.ending, state.endingKeywords, null, null, previous?.ending),
    meta: state.meta, metaTokens: state.metaTokens, totalTokens: state.totalTokens, readOnly: state.readOnly,
    fieldErrors: state.fieldErrors, formErrors: state.formErrors, keywordLimitHit: state.keywordLimitHit,
    saveBar: { state: state.saving ? 'saving' : state.errorKey !== null ? 'error' : state.isDirty ? 'dirty' : state.savedAt !== null ? 'saved' : 'clean', disabled: state.saveDisabled, errorKey: state.errorKey },
  };
}
