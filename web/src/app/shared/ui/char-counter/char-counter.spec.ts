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
});
