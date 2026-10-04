import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoTestingModule, provideTranslocoScope } from '@jsverse/transloco';
import { describe, expect, it } from 'vitest';
import coreEn from '../../../../../assets/i18n/core/en.json';
import coreVi from '../../../../../assets/i18n/core/vi.json';
import publicEn from '../../../../../assets/i18n/public/en.json';
import publicVi from '../../../../../assets/i18n/public/vi.json';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import type { LegalDocument } from '../../domain/legal.model';
import { LegalRepository } from '../../domain/legal.port';
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

async function render(repository: FakeLegalRepository): Promise<ComponentFixture<LegalDocumentPage>> {
  await TestBed.configureTestingModule({
    imports: [LegalDocumentPage, translations],
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([]),
      provideTranslocoScope('public'),
      { provide: LegalRepository, useValue: repository },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(LegalDocumentPage);
  fixture.componentRef.setInput('doc', 'privacy');
  fixture.componentRef.setInput('lang', 'vi');
  await fixture.whenStable();
  return fixture;
}

describe('LegalDocumentPage', () => {
  it('looks up the bound doc and lang and passes the document to the legal view', async () => {
    const repository = new FakeLegalRepository();
    repository.set(legalDocument);
    const fixture = await render(repository);
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('h1')?.textContent).toContain(legalDocument.title);
    expect(root.querySelector('[data-legal-block]')?.textContent).toContain('Repository content');
  });

  it('maps legal_content_missing to the legal EmptyState', async () => {
    const fixture = await render(new FakeLegalRepository());
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('nts-empty-state')).not.toBeNull();
    expect(root.textContent).toContain(publicVi.legal.empty);
    expect(root.querySelector('[data-legal-block]')).toBeNull();
  });
});
