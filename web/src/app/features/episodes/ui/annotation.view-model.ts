import type { InlineAnnotationViewModel } from '@shared/ui';
import { parseAddress } from '@shared/util/segment-address';
import type { SegmentAddress } from '@shared/util/segment-address';
import type { SegmentAnnotation } from '../domain/annotation.model';

export interface AnnotationPanelViewModel {
  readonly count: number;
  readonly groups: readonly { readonly kind: 'objective' | 'taste'; readonly items: readonly InlineAnnotationViewModel[] }[];
}
export interface AnnotationViews {
  readonly inline: ReadonlyMap<SegmentAddress, readonly InlineAnnotationViewModel[]>;
  readonly flaggedChips: ReadonlyMap<SegmentAddress, ReadonlyMap<number, { readonly annotationId: string; readonly messageKey: string }>>;
  readonly panel: AnnotationPanelViewModel;
}
export const EMPTY_ANNOTATION_VIEWS: AnnotationViews = {
  inline: new Map(), flaggedChips: new Map(), panel: { count: 0, groups: [] },
};
export interface AnnotationViewOptions {
  readonly hasKey: (key: string) => boolean;
  readonly warnUnknown?: (key: string) => void;
}
const warnedKeys = new Set<string>();

function equalItem(left: InlineAnnotationViewModel, right: InlineAnnotationViewModel): boolean {
  return left.id === right.id && left.address === right.address && left.ruleId === right.ruleId &&
    left.severity === right.severity && left.kind === right.kind && left.messageKey === right.messageKey &&
    left.evidence === right.evidence && left.repairDirection === right.repairDirection &&
    left.addressLabelKey === right.addressLabelKey && left.addressLabelIndex === right.addressLabelIndex && left.canGoTo === right.canGoTo;
}
function reuseItems(items: readonly InlineAnnotationViewModel[], previous?: readonly InlineAnnotationViewModel[]): readonly InlineAnnotationViewModel[] {
  return previous !== undefined && previous.length === items.length && items.every((item, index) => equalItem(item, previous[index]!)) ? previous : items;
}

/** Saved feedback only: callers rebuild on annotations/language changes, not draft text. */
export function buildAnnotationViews(annotations: readonly SegmentAnnotation[], options: AnnotationViewOptions, previous?: AnnotationViews): AnnotationViews {
  if (annotations.length === 0) return EMPTY_ANNOTATION_VIEWS;
  const inline = new Map<SegmentAddress, InlineAnnotationViewModel[]>();
  const flags = new Map<SegmentAddress, Map<number, { readonly annotationId: string; readonly messageKey: string }>>();
  const objective: InlineAnnotationViewModel[] = [], taste: InlineAnnotationViewModel[] = [];
  for (const annotation of annotations) {
    const parsed = annotation.address === null ? null : parseAddress(annotation.address);
    const address = parsed === null ? null : annotation.address;
    let messageKey = annotation.messageKey;
    if (!/^[A-Za-z0-9._:-]+$/.test(messageKey) || !options.hasKey(messageKey)) {
      if (options.warnUnknown !== undefined && !warnedKeys.has(messageKey)) {
        warnedKeys.add(messageKey);
        options.warnUnknown(messageKey);
      }
      messageKey = 'episodes.annotations.unknown';
    }
    const item: InlineAnnotationViewModel = {
      id: annotation.id, address, ruleId: annotation.ruleId, severity: annotation.severity, kind: annotation.kind,
      messageKey, evidence: annotation.evidence, repairDirection: annotation.repairDirection,
      addressLabelKey: address === null ? 'episodes.annotations.unresolved' : address === 'meta' ? 'episodes.annotations.meta' : `episodes.editor.segment.${parsed?.kind}`,
      addressLabelIndex: parsed?.kind === 'paragraph' && parsed.index !== null ? parsed.index + 1 : null,
      canGoTo: address !== null && address !== 'meta' && annotation.placement === 'inline',
    };
    (annotation.kind === 'objective' ? objective : taste).push(item);
    if (annotation.placement !== 'inline' || address === null || address === 'meta') continue;
    if (annotation.source === 'keyword' && annotation.keywordIndex !== null) {
      let chips = flags.get(address);
      if (chips === undefined) { chips = new Map(); flags.set(address, chips); }
      if (!chips.has(annotation.keywordIndex)) chips.set(annotation.keywordIndex, { annotationId: annotation.id, messageKey });
    } else {
      let items = inline.get(address);
      if (items === undefined) { items = []; inline.set(address, items); }
      items.push(item);
    }
  }
  const stableInline = new Map<SegmentAddress, readonly InlineAnnotationViewModel[]>();
  for (const [address, items] of inline) stableInline.set(address, reuseItems(items, previous?.inline.get(address)));
  const stableFlags = new Map<SegmentAddress, ReadonlyMap<number, { readonly annotationId: string; readonly messageKey: string }>>();
  for (const [address, chips] of flags) {
    const old = previous?.flaggedChips.get(address);
    stableFlags.set(address, old !== undefined && old.size === chips.size && [...chips].every(([index, flag]) => old.get(index)?.annotationId === flag.annotationId && old.get(index)?.messageKey === flag.messageKey) ? old : chips);
  }
  const groups: AnnotationPanelViewModel['groups'] = [
    ...(objective.length > 0 ? [{ kind: 'objective' as const, items: reuseItems(objective, previous?.panel.groups.find((group) => group.kind === 'objective')?.items) }] : []),
    ...(taste.length > 0 ? [{ kind: 'taste' as const, items: reuseItems(taste, previous?.panel.groups.find((group) => group.kind === 'taste')?.items) }] : []),
  ];
  const oldPanel = previous?.panel;
  const panel = oldPanel !== undefined && oldPanel.count === annotations.length && oldPanel.groups.length === groups.length && groups.every((group, index) => oldPanel.groups[index]?.kind === group.kind && oldPanel.groups[index]?.items === group.items) ? oldPanel : { count: annotations.length, groups };
  return { inline: stableInline, flaggedChips: stableFlags, panel };
}
