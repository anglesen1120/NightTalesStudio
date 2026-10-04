import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { uiTestingTranslations } from '../testing/ui-testing-translations';
import { CharCounter } from './char-counter';

@Component({ imports: [CharCounter], template: '<input aria-describedby="counter"><nts-char-counter id="counter" [value]="value" [max]="10" />' })
class Host { value = 0; }

describe('CharCounter', () => {
  for (const [value, over] of [[9, false], [10, false], [11, true]] as const) {
    it(`renders ${value}/10 with over=${over}`, async () => {
      await TestBed.configureTestingModule({ imports: [Host, uiTestingTranslations()] }).compileComponents();
      const fixture = TestBed.createComponent(Host);
      fixture.componentInstance.value = value;
      fixture.detectChanges();
      await fixture.whenStable();
      const counter = fixture.nativeElement.querySelector('nts-char-counter');
      expect(counter.textContent).toContain(`${value}/10`);
      expect(counter.classList.contains('char-counter--over')).toBe(over);
      expect(counter.querySelector('[aria-live]')).toBeNull();
      expect(fixture.nativeElement.querySelector('input').getAttribute('aria-describedby')).toBe(counter.querySelector('span').id);
    });
  }

  for (const [value, announced] of [[10, false], [11, true]] as const) {
    it(`${announced ? 'announces' : 'does not announce'} the over-limit text at ${value}/10`, async () => {
      await TestBed.configureTestingModule({ imports: [Host, uiTestingTranslations()] }).compileComponents();
      const fixture = TestBed.createComponent(Host);
      fixture.componentInstance.value = value;
      fixture.detectChanges();
      await fixture.whenStable();
      const text: string = fixture.nativeElement.querySelector('nts-char-counter').textContent;
      expect(text.includes('vượt giới hạn')).toBe(announced);
    });
  }

  it('gives each instance a distinct default id that exists exactly once in the DOM', async () => {
    @Component({ imports: [CharCounter], template: '<nts-char-counter [value]="1" [max]="10" /><nts-char-counter [value]="2" [max]="10" />' })
    class TwoDefaults {}
    await TestBed.configureTestingModule({ imports: [TwoDefaults, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(TwoDefaults);
    fixture.detectChanges();
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    const hosts = Array.from(root.querySelectorAll('nts-char-counter'));
    const ids = hosts.map((host) => host.querySelector('span')?.id ?? '');
    expect(ids[0]).not.toBe('');
    expect(ids[1]).not.toBe('');
    expect(ids[0]).not.toBe(ids[1]);
    for (const host of hosts) expect(host.hasAttribute('id')).toBe(false);
    for (const id of ids) expect(root.querySelectorAll(`[id="${id}"]`)).toHaveLength(1);
  });

  it('renders an explicit id exactly once, on the inner span and not on the host', async () => {
    await TestBed.configureTestingModule({ imports: [Host, uiTestingTranslations()] }).compileComponents();
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    await fixture.whenStable();
    const matches = fixture.nativeElement.querySelectorAll('[id="counter"]');
    expect(matches).toHaveLength(1);
    expect(matches[0].tagName).toBe('SPAN');
    expect(fixture.nativeElement.querySelector('nts-char-counter').hasAttribute('id')).toBe(false);
  });
});
