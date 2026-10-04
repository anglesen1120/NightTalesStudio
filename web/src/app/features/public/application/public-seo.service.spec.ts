import { DOCUMENT } from '@angular/common';
import { TestBed } from '@angular/core/testing';
import { Meta, Title } from '@angular/platform-browser';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import type { LegalDocument, LegalLang } from '../domain/legal.model';
import { PublicSeoService, describeLegalDocument } from './public-seo.service';

function doc(over: Partial<LegalDocument> = {}): LegalDocument {
  return {
    doc: 'privacy',
    lang: 'vi',
    title: 'Chính sách quyền riêng tư',
    version: '1.0.0',
    effectiveDate: '2026-10-01',
    blocks: [
      { id: 'block-1', kind: 'heading', level: 2, runs: [{ kind: 'text', text: 'Mục một' }] },
      { id: 'block-2', kind: 'paragraph', runs: [{ kind: 'text', text: 'Đoạn ' }, { kind: 'emphasis', text: 'đầu' }] },
    ],
    ...over,
  };
}

const alternatesOf = (name: string): Record<LegalLang, string> => ({ vi: `/vi/${name}`, en: `/en/${name}` });

function setup(): { seo: PublicSeoService; head: HTMLHeadElement; meta: Meta; title: Title } {
  TestBed.configureTestingModule({ providers: [...ZONELESS_TEST_PROVIDERS] });
  return {
    seo: TestBed.inject(PublicSeoService),
    head: TestBed.inject(DOCUMENT).head,
    meta: TestBed.inject(Meta),
    title: TestBed.inject(Title),
  };
}

function owned(head: HTMLHeadElement, selector: string): Element[] {
  return Array.from(head.querySelectorAll(`link[data-nts-seo]${selector}`));
}

describe('PublicSeoService', () => {
  afterEach(() => {
    document.head.querySelectorAll('link[data-nts-seo], meta[name="description"], meta[property^="og:"]').forEach((e) => e.remove());
  });

  it('sets title, description, og:* and the canonical + hreflang links once', () => {
    const { seo, head, meta, title } = setup();
    seo.apply(doc(), alternatesOf('privacy'));

    expect(title.getTitle()).toBe('Chính sách quyền riêng tư · NightTales Studio');
    expect(meta.getTag('name="description"')?.content).toBe('Đoạn đầu');
    expect(meta.getTag('property="og:title"')?.content).toBe('Chính sách quyền riêng tư · NightTales Studio');
    expect(meta.getTag('property="og:description"')?.content).toBe('Đoạn đầu');
    expect(meta.getTag('property="og:type"')?.content).toBe('article');
    expect(meta.getTag('property="og:locale"')?.content).toBe('vi_VN');
    expect(owned(head, '[rel="canonical"]').map((e) => e.getAttribute('href'))).toEqual(['/vi/privacy']);
    expect(owned(head, '[rel="alternate"]').map((e) => [e.getAttribute('hreflang'), e.getAttribute('href')])).toEqual([
      ['vi', '/vi/privacy'],
      ['en', '/en/privacy'],
      ['x-default', '/vi/privacy'],
    ]);
  });

  it('canonical follows the document language while x-default stays on vi', () => {
    const { seo, head, meta } = setup();
    seo.apply(doc({ lang: 'en', title: 'Privacy policy' }), alternatesOf('privacy'));
    expect(owned(head, '[rel="canonical"]').map((e) => e.getAttribute('href'))).toEqual(['/en/privacy']);
    expect(owned(head, '[hreflang="x-default"]').map((e) => e.getAttribute('href'))).toEqual(['/vi/privacy']);
    expect(meta.getTag('property="og:locale"')?.content).toBe('en_US');
  });

  it('does not duplicate anything when apply runs twice and updates the values for another document', () => {
    const { seo, head, title } = setup();
    seo.apply(doc(), alternatesOf('privacy'));
    seo.apply(doc({ doc: 'terms', title: 'Điều khoản sử dụng' }), alternatesOf('terms'));

    expect(title.getTitle()).toBe('Điều khoản sử dụng · NightTales Studio');
    expect(head.querySelectorAll('meta[name="description"]')).toHaveLength(1);
    expect(head.querySelectorAll('meta[property="og:title"]')).toHaveLength(1);
    expect(head.querySelectorAll('meta[property="og:description"]')).toHaveLength(1);
    expect(head.querySelectorAll('meta[property="og:type"]')).toHaveLength(1);
    expect(head.querySelectorAll('meta[property="og:locale"]')).toHaveLength(1);
    expect(owned(head, '[rel="canonical"]').map((e) => e.getAttribute('href'))).toEqual(['/vi/terms']);
    expect(owned(head, '[rel="alternate"]')).toHaveLength(3);
    expect(owned(head, '[rel="alternate"][hreflang="en"]').map((e) => e.getAttribute('href'))).toEqual(['/en/terms']);
  });

  it('leaves a link it does not own alone', () => {
    const { seo, head } = setup();
    const foreign = document.createElement('link');
    foreign.setAttribute('rel', 'canonical');
    foreign.setAttribute('href', '/foreign');
    head.appendChild(foreign);
    seo.apply(doc(), alternatesOf('privacy'));
    seo.apply(doc(), alternatesOf('privacy'));
    expect(head.contains(foreign)).toBe(true);
    foreign.remove();
  });
});

describe('describeLegalDocument', () => {
  it('uses the first non-empty paragraph as plain text', () => {
    const blocks: LegalDocument['blocks'] = [
      { id: 'block-1', kind: 'paragraph', runs: [{ kind: 'text', text: '  ' }] },
      { id: 'block-2', kind: 'paragraph', runs: [{ kind: 'link', text: 'A', href: '/x' }, { kind: 'text', text: '  b\n c' }] },
    ];
    expect(describeLegalDocument(doc({ blocks }))).toBe('A b c');
  });

  it('cuts a long paragraph at a word boundary within 160 characters and adds an ellipsis', () => {
    const blocks: LegalDocument['blocks'] = [
      { id: 'block-1', kind: 'paragraph', runs: [{ kind: 'text', text: 'word '.repeat(60) }] },
    ];
    const text = describeLegalDocument(doc({ blocks }));
    expect(text.length).toBeLessThanOrEqual(160);
    expect(text.endsWith('word…')).toBe(true);
  });

  it('falls back to the title when there is no paragraph', () => {
    expect(describeLegalDocument(doc({ blocks: [] }))).toBe('Chính sách quyền riêng tư');
  });
});
