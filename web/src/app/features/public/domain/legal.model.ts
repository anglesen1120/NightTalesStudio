/** The published legal documents (route segment names; manifest key `tos` maps to `terms`, 13 CR-34). */
export type LegalDoc = 'privacy' | 'terms';

/** The two languages every legal document ships in. */
export type LegalLang = 'vi' | 'en';

export const LEGAL_DOCS: readonly LegalDoc[] = ['privacy', 'terms'];
export const LEGAL_LANGS: readonly LegalLang[] = ['vi', 'en'];

/** One inline run of text; `href` is already restricted at build time to `https://`, `mailto:` or a site-relative `/`. */
export type LegalRun =
  | { readonly kind: 'text'; readonly text: string }
  | { readonly kind: 'link'; readonly text: string; readonly href: string }
  | { readonly kind: 'emphasis'; readonly text: string };

export interface LegalBlock {
  readonly id: string;
  readonly kind: 'heading' | 'paragraph' | 'list' | 'quote';
  /** Set for `heading` only. */
  readonly level?: 2 | 3;
  /** Set for `heading`, `paragraph` and `quote`. */
  readonly runs?: readonly LegalRun[];
  /** Set for `list` only: one run list per item. */
  readonly items?: readonly (readonly LegalRun[])[];
  /** Set for `list` only: `true` for a `1.` list. */
  readonly ordered?: boolean;
}

export interface LegalDocument {
  readonly doc: LegalDoc;
  readonly lang: LegalLang;
  /** The Markdown H1 of the source file (plain text). */
  readonly title: string;
  readonly version: string;
  /** ISO date `YYYY-MM-DD` from `manifest.json`. */
  readonly effectiveDate: string;
  readonly blocks: readonly LegalBlock[];
}
