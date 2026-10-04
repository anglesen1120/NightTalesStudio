import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { uiTestingTranslations } from '../testing/ui-testing-translations';
import { PageHeader } from './page-header';

@Component({ template: '<p>safe</p>' })
class SafePage {}

@Component({ imports: [PageHeader], template: '<nts-page-header titleKey="core.ui.emptyState.title" [backHref]="href" />' })
class Host { href = '/safe'; }

describe('PageHeader', () => {
  it('renders one h1 and navigates a safe app-relative path', async () => {
    await TestBed.configureTestingModule({ imports: [Host, uiTestingTranslations()], providers: [provideRouter([{ path: 'safe', component: SafePage }])] }).compileComponents();
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelectorAll('h1')).toHaveLength(1);
    (fixture.nativeElement.querySelector('a') as HTMLAnchorElement).click();
    await fixture.whenStable();
    expect(TestBed.inject(Router).url).toBe('/safe');
  });

  for (const hostile of ['//evil.example', 'https://evil.example', '\\evil', '//scheme-relative']) {
    it(`rejects hostile backHref ${hostile}`, async () => {
      await TestBed.configureTestingModule({ imports: [Host, uiTestingTranslations()], providers: [provideRouter([])] }).compileComponents();
      const fixture = TestBed.createComponent(Host);
      fixture.componentInstance.href = hostile;
      fixture.detectChanges();
      await fixture.whenStable();
      expect(fixture.nativeElement.querySelector('a')).toBeNull();
      expect(TestBed.inject(Router).url).toBe('/');
    });
  }

  for (const nonPath of ['javascript:alert(1)', 'data:text/html,x', 'relative/path', '']) {
    it(`rejects non-absolute-path backHref "${nonPath}"`, async () => {
      await TestBed.configureTestingModule({ imports: [Host, uiTestingTranslations()], providers: [provideRouter([])] }).compileComponents();
      const fixture = TestBed.createComponent(Host);
      fixture.componentInstance.href = nonPath;
      fixture.detectChanges();
      await fixture.whenStable();
      expect(fixture.nativeElement.querySelector('a')).toBeNull();
    });
  }

  for (const smuggled of ['/\t/evil.example', '/\n/evil.example']) {
    it(`never renders a cross-origin href for ${JSON.stringify(smuggled)}`, async () => {
      await TestBed.configureTestingModule({ imports: [Host, uiTestingTranslations()], providers: [provideRouter([])] }).compileComponents();
      const fixture = TestBed.createComponent(Host);
      fixture.componentInstance.href = smuggled;
      fixture.detectChanges();
      await fixture.whenStable();
      const anchor = fixture.nativeElement.querySelector('a') as HTMLAnchorElement | null;
      if (anchor !== null) {
        expect(new URL(anchor.href, document.baseURI).origin).toBe(location.origin);
      }
    });
  }
});
