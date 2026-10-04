import { Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import * as UiKit from '../index';
import { uiTestingTranslations } from '../testing/ui-testing-translations';
import { IconAlert } from './icon-alert/icon-alert';
import { IconCheck } from './icon-check/icon-check';
import { IconClock } from './icon-clock/icon-clock';
import { IconCopy } from './icon-copy/icon-copy';
import { IconPause } from './icon-pause/icon-pause';
import { IconPlay } from './icon-play/icon-play';
import { IconUnknown } from './icon-unknown/icon-unknown';

const icons: readonly [Type<unknown>, string][] = [
  [IconCheck, 'core.ui.icons.check'], [IconClock, 'core.ui.icons.clock'], [IconAlert, 'core.ui.icons.alert'],
  [IconPlay, 'core.ui.icons.play'], [IconPause, 'core.ui.icons.pause'], [IconCopy, 'core.ui.icons.copy'],
  [IconUnknown, 'core.ui.icons.unknown'],
];

describe('inline icons', () => {
  for (const [icon, labelKey] of icons) {
    it(`${icon.name} renders one decorative or labelled inline svg`, async () => {
      await TestBed.configureTestingModule({ imports: [icon, uiTestingTranslations()] }).compileComponents();
      const fixture = TestBed.createComponent(icon);
      fixture.detectChanges();
      let svg = fixture.nativeElement.querySelector('svg') as SVGElement;
      expect(fixture.nativeElement.querySelectorAll('svg')).toHaveLength(1);
      expect(svg.getAttribute('aria-hidden')).toBe('true');
      expect(svg.getAttribute('role')).toBeNull();
      fixture.componentRef.setInput('labelKey', labelKey);
      fixture.detectChanges();
      await fixture.whenStable();
      svg = fixture.nativeElement.querySelector('svg');
      expect(svg.getAttribute('aria-hidden')).toBeNull();
      expect(svg.getAttribute('role')).toBe('img');
      expect(svg.getAttribute('aria-label')).toBeTruthy();
    });
  }

  it('does not expose an icon registry or resource bypass API', () => {
    const api = Object.keys(UiKit).join(' ');
    expect(api).not.toContain('MatIconRegistry');
    expect(api).not.toContain('bypassSecurityTrustResourceUrl');
  });
});
