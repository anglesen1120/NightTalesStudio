import { DOCUMENT } from '@angular/common';
import { Injectable, inject } from '@angular/core';
import { Meta, Title } from '@angular/platform-browser';
import type { LegalDocument, LegalLang } from '../domain/legal.model';

/** The suffix of the app title (`<title>` in index.html). */
const SITE_NAME = 'NightTales Studio';
/** Marks the `<link>` elements this service owns, so a second `apply` replaces them instead of adding more. */
const OWNED_ATTRIBUTE = 'data-nts-seo';
const DESCRIPTION_MAX = 160;
const OG_LOCALE: Readonly<Record<LegalLang, string>> = { vi: 'vi_VN', en: 'en_US' };
/** `x-default` points at the default language of README D10. */
const X_DEFAULT_LANG: LegalLang = 'vi';

/** The first non-empty paragraph, whitespace collapsed and cut at a word boundary to at most 160 characters. */
export function describeLegalDocument(doc: LegalDocument): string {
  const text =
    doc.blocks
      .filter((block) => block.kind === 'paragraph')
      .map((block) => (block.runs ?? []).map((run) => run.text).join('').replace(/\s+/g, ' ').trim())
      .find((paragraph) => paragraph !== '') ?? '';
  if (text === '') return doc.title;
  if (text.length <= DESCRIPTION_MAX) return text;
  const cut = text.slice(0, DESCRIPTION_MAX - 1);
  const lastSpace = cut.lastIndexOf(' ');
  return `${(lastSpace > 0 ? cut.slice(0, lastSpace) : cut).trimEnd()}…`;
}

/**
 * Per-page head metadata of the public pages (README D10/D12): `<title>`, `description`, `og:*`, `<link rel=canonical>`
 * and the `hreflang` alternates. Everything is written through `Title`, `Meta` and DOM calls on `DOCUMENT.head`
 * (no `innerHTML`), so it lands in the prerendered HTML. Idempotent: values are updated in place and the owned
 * `<link>` elements are removed before they are added again.
 */
@Injectable({ providedIn: 'root' })
export class PublicSeoService {
  private readonly document = inject(DOCUMENT);
  private readonly title = inject(Title);
  private readonly meta = inject(Meta);

  /**
   * `alternates` maps each language to its site-relative path (`/vi/privacy`): there is no build-time site origin in
   * the web contract, so the hrefs stay root-relative rather than invent a domain. The canonical is the alternate of
   * the document's own language.
   */
  apply(doc: LegalDocument, alternates: Readonly<Record<LegalLang, string>>): void {
    const title = `${doc.title} · ${SITE_NAME}`;
    const description = describeLegalDocument(doc);
    this.title.setTitle(title);
    this.meta.updateTag({ name: 'description', content: description });
    this.meta.updateTag({ property: 'og:title', content: title });
    this.meta.updateTag({ property: 'og:description', content: description });
    this.meta.updateTag({ property: 'og:type', content: 'article' });
    this.meta.updateTag({ property: 'og:locale', content: OG_LOCALE[doc.lang] });

    const head = this.document.head;
    for (const owned of Array.from(head.querySelectorAll(`link[${OWNED_ATTRIBUTE}]`))) owned.remove();
    head.appendChild(this.link('canonical', alternates[doc.lang]));
    head.appendChild(this.link('alternate', alternates.vi, 'vi'));
    head.appendChild(this.link('alternate', alternates.en, 'en'));
    head.appendChild(this.link('alternate', alternates[X_DEFAULT_LANG], 'x-default'));
  }

  private link(rel: 'canonical' | 'alternate', href: string, hreflang?: string): HTMLLinkElement {
    const element = this.document.createElement('link');
    element.setAttribute('rel', rel);
    element.setAttribute('href', href);
    if (hreflang !== undefined) element.setAttribute('hreflang', hreflang);
    element.setAttribute(OWNED_ATTRIBUTE, '');
    return element;
  }
}
