import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { TranslocoTestingModule, provideTranslocoScope } from '@jsverse/transloco';
import { describe, expect, it } from 'vitest';
import coreEn from '../../../../../assets/i18n/core/en.json';
import coreVi from '../../../../../assets/i18n/core/vi.json';
import publicEn from '../../../../../assets/i18n/public/en.json';
import publicVi from '../../../../../assets/i18n/public/vi.json';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import type { LegalDoc, LegalDocument, LegalLang } from '../../domain/legal.model';
import type { LegalRepository } from '../../domain/legal.port';
import { LEGAL_CONTENT } from '../../data-access/legal-content.generated';
import { GeneratedLegalRepository } from '../../data-access/legal.repository';
import { FakeLegalRepository } from '../../testing/fake-legal.repository';
import { LegalDocumentPage } from './legal-document-page';

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

const legalDocument: LegalDocument = {
  doc: 'privacy',
  lang: 'vi',
  title: 'Authored privacy title',
  version: '1.2.3',
  effectiveDate: '2026-10-02',
  blocks: [
    {
      id: 'block-1',
      kind: 'paragraph',
      runs: [{ kind: 'text', text: 'Repository content' }],
    },
  ],
};

// Mirrors what `legalResolver` hands the page: the looked-up document, or `null` for `legal_content_missing`.
function lookup(repository: LegalRepository, doc: string, lang: string): LegalDocument | null {
  try {
    return repository.get(doc as LegalDoc, lang as LegalLang);
  } catch (error: unknown) {
    if (error instanceof Error && error.message === 'legal_content_missing') return null;
    throw error;
  }
}

// The page reads `doc`, `lang` and the resolved `legal` from ActivatedRoute data; `lookups` proves one lookup per route.
async function render(
  repository: LegalRepository,
  doc: string = 'privacy',
  lang: string = 'vi',
): Promise<{ root: HTMLElement; lookups: number }> {
  const state = { lookups: 0 };
  await TestBed.configureTestingModule({
    imports: [translations],
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([
        {
          path: 'legal',
          component: LegalDocumentPage,
          data: { doc, lang },
          resolve: {
            legal: () => {
              state.lookups += 1;
              return lookup(repository, doc, lang);
            },
          },
          providers: [provideTranslocoScope('public')],
        },
      ]),
    ],
  }).compileComponents();
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl('/legal', LegalDocumentPage);
  await harness.fixture.whenStable();
  return { root: harness.routeNativeElement as HTMLElement, lookups: state.lookups };
}

describe('LegalDocumentPage', () => {
  it('shows the document the route resolved, with a single lookup', async () => {
    const repository = new FakeLegalRepository();
    repository.set(legalDocument);
    const { root, lookups } = await render(repository);

    expect(lookups).toBe(1);
    expect(root.querySelector('h1')?.textContent).toContain(legalDocument.title);
    expect(root.querySelector('[data-legal-block]')?.textContent).toContain('Repository content');
  });

  it('renders the legal EmptyState when the resolved document is null (legal_content_missing)', async () => {
    const { root } = await render(new FakeLegalRepository());

    expect(root.querySelector('nts-empty-state')).not.toBeNull();
    expect(root.textContent).toContain(publicVi.legal.empty);
    expect(root.querySelector('[data-legal-block]')).toBeNull();
  });

  it.each(['__proto__', 'constructor', 'toString'])(
    'renders the EmptyState for the inherited property name %s with the generated repository',
    async (doc) => {
      const { root } = await render(new GeneratedLegalRepository(), doc);

      expect(root.querySelector('nts-empty-state')).not.toBeNull();
      expect(root.querySelector('[data-legal-block]')).toBeNull();
    },
  );

  it.each([
    ['privacy', 'vi'],
    ['privacy', 'en'],
    ['terms', 'vi'],
    ['terms', 'en'],
  ] as const)('renders the committed %s/%s document through the generated repository', async (doc, lang) => {
    const { root } = await render(new GeneratedLegalRepository(), doc, lang);
    const expected = LEGAL_CONTENT[doc][lang];

    expect(root.querySelector('h1')?.textContent).toContain(expected.title);
    expect(root.querySelectorAll('[data-legal-block]').length).toBe(expected.blocks.length);
    expect(root.querySelector('nts-empty-state')).toBeNull();
    expect(root.textContent).toContain(expected.version);
  });
});
