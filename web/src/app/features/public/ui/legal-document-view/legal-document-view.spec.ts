import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoTestingModule, provideTranslocoScope } from '@jsverse/transloco';
import { describe, expect, it } from 'vitest';
import coreEn from '../../../../../assets/i18n/core/en.json';
import coreVi from '../../../../../assets/i18n/core/vi.json';
import publicEn from '../../../../../assets/i18n/public/en.json';
import publicVi from '../../../../../assets/i18n/public/vi.json';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import type { LegalDocumentViewModel } from '../../domain/legal-document-view.model';
import type { LegalDoc, LegalDocument, LegalLang } from '../../domain/legal.model';
import { LegalDocumentView } from './legal-document-view';

const translations = TranslocoTestingModule.forRoot({
  langs: {
    vi: coreVi,
    en: coreEn,
    'public/vi': publicVi,
    'public/en': publicEn,
  },
  translocoConfig: {
    availableLangs: ['vi', 'en'],
    defaultLang: 'vi',
    fallbackLang: 'vi',
    reRenderOnLangChange: true,
  },
  preloadLangs: true,
});

const privacyDocument: LegalDocument = {
  doc: 'privacy',
  lang: 'vi',
  title: 'Privacy policy',
  version: '1.2.3',
  effectiveDate: '2026-10-02',
  blocks: [
    { id: 'block-1', kind: 'heading', level: 2, runs: [{ kind: 'text', text: 'Collection' }] },
    {
      id: 'block-2',
      kind: 'paragraph',
      runs: [
        { kind: 'text', text: 'We keep ' },
        { kind: 'emphasis', text: 'only required data' },
        { kind: 'text', text: '. Read ' },
        { kind: 'link', text: 'the source', href: 'https://example.test/source' },
        { kind: 'text', text: '.' },
      ],
    },
    {
      id: 'block-3',
      kind: 'list',
      items: [
        [{ kind: 'text', text: 'Account details' }],
        [{ kind: 'text', text: 'Studio activity' }],
      ],
    },
    {
      id: 'block-4',
      kind: 'list',
      ordered: true,
      items: [
        [{ kind: 'text', text: 'Request access' }],
        [{ kind: 'text', text: 'Receive export' }],
      ],
    },
    { id: 'block-5', kind: 'quote', runs: [{ kind: 'text', text: 'Your choices remain yours.' }] },
    { id: 'block-6', kind: 'heading', level: 3, runs: [{ kind: 'text', text: 'Contact' }] },
  ],
};

function documentFor(doc: LegalDoc, lang: LegalLang, blocks = privacyDocument.blocks): LegalDocument {
  return {
    doc,
    lang,
    title: doc === 'privacy' ? 'Privacy policy' : 'Terms of service',
    version: '1.2.3',
    effectiveDate: '2026-10-02',
    blocks,
  };
}

function viewModelFor(document: LegalDocument): LegalDocumentViewModel {
  const otherLang: LegalLang = document.lang === 'vi' ? 'en' : 'vi';
  return {
    doc: document.doc,
    document,
    toc: document.blocks.filter((block) => block.kind === 'heading'),
    otherLang,
    siblingRoute: ['/', otherLang, document.doc],
  };
}

async function render(viewModel: LegalDocumentViewModel): Promise<ComponentFixture<LegalDocumentView>> {
  await TestBed.configureTestingModule({
    imports: [LegalDocumentView, translations],
    providers: [...ZONELESS_TEST_PROVIDERS, provideRouter([]), provideTranslocoScope('public')],
  }).compileComponents();
  const fixture = TestBed.createComponent(LegalDocumentView);
  fixture.componentRef.setInput('viewModel', viewModel);
  await fixture.whenStable();
  return fixture;
}

describe('LegalDocumentView', () => {
  it('renders every legal block and inline run one-to-one in source order', async () => {
    const fixture = await render(viewModelFor(privacyDocument));
    const root = fixture.nativeElement as HTMLElement;
    const blocks = [...root.querySelectorAll('[data-legal-block]')];

    expect(root.querySelector('h1')?.textContent).toContain(privacyDocument.title);
    expect(blocks.map((block) => block.tagName)).toEqual([
      'H2',
      'P',
      'UL',
      'OL',
      'BLOCKQUOTE',
      'H3',
    ]);
    const text = (node: Element): string => (node.textContent ?? '').replace(/\s+/g, ' ').trim();
    expect(
      blocks.map((block) =>
        block.tagName === 'UL' || block.tagName === 'OL'
          ? [...block.querySelectorAll('li')].map(text).join(' | ')
          : text(block),
      ),
    ).toEqual([
      'Collection',
      'We keep only required data. Read the source.',
      'Account details | Studio activity',
      'Request access | Receive export',
      'Your choices remain yours.',
      'Contact',
    ]);
    expect(blocks[1]?.querySelector('strong')?.textContent).toBe('only required data');
    const externalLink = blocks[1]?.querySelector<HTMLAnchorElement>('a');
    expect(externalLink?.getAttribute('href')).toBe('https://example.test/source');
    expect(externalLink?.getAttribute('rel')).toBe('noopener noreferrer');
    expect(blocks[2]?.querySelectorAll('li')).toHaveLength(2);
    expect(blocks[3]?.querySelectorAll('li')).toHaveLength(2);
  });

  it('shows the version and the ISO effective date in semantic metadata', async () => {
    const fixture = await render(viewModelFor(privacyDocument));
    const root = fixture.nativeElement as HTMLElement;
    const time = root.querySelector('time');

    expect(root.textContent).toContain('Phiên bản 1.2.3');
    expect(time?.getAttribute('datetime')).toBe('2026-10-02');
    expect(time?.textContent).toContain('2026-10-02');
  });

  it('renders EmptyState when the document has no blocks while preserving metadata', async () => {
    const fixture = await render(viewModelFor(documentFor('privacy', 'vi', [])));
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('nts-empty-state')).not.toBeNull();
    expect(root.textContent).toContain(publicVi.legal.empty);
    expect(root.textContent).toContain('Phiên bản 1.2.3');
    expect(root.querySelectorAll('h1')).toHaveLength(1);
  });

  for (const [lang, sibling] of [
    ['vi', 'en'],
    ['en', 'vi'],
  ] as const) {
    it(`links ${lang} to the ${sibling} sibling of the same document`, async () => {
      const fixture = await render(viewModelFor(documentFor('terms', lang)));
      const link = (fixture.nativeElement as HTMLElement).querySelector<HTMLAnchorElement>(
        `a[hreflang="${sibling}"]`,
      );

      expect(link?.getAttribute('href')).toBe(`/${sibling}/terms`);
      expect(link?.getAttribute('hreflang')).toBe(sibling);
      expect(link?.getAttribute('lang')).toBe(sibling);
    });
  }

  it('links every table-of-contents entry to an existing heading id', async () => {
    const fixture = await render(viewModelFor(privacyDocument));
    const root = fixture.nativeElement as HTMLElement;
    const links = [...root.querySelectorAll<HTMLAnchorElement>('nav[aria-labelledby] a')];

    expect(links.map((link) => link.getAttribute('href'))).toEqual(['#block-1', '#block-6']);
    for (const link of links) {
      const href = link.getAttribute('href');
      expect(href).not.toBeNull();
      expect(root.querySelector(href as string)).not.toBeNull();
    }
  });
});
