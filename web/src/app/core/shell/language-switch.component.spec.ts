import { By } from '@angular/platform-browser';
import { TestBed } from '@angular/core/testing';
import { MatSelect } from '@angular/material/select';
import { describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { onEmit } from '@shared/ui/testing/on-emit';
import { uiTestingTranslations } from '@shared/ui/testing/ui-testing-translations';
import { LanguageSwitchComponent } from './language-switch.component';

async function createLanguageSwitch() {
  await TestBed.configureTestingModule({
    imports: [LanguageSwitchComponent, uiTestingTranslations('en')],
    providers: ZONELESS_TEST_PROVIDERS,
  }).compileComponents();
  const fixture = TestBed.createComponent(LanguageSwitchComponent);
  fixture.componentRef.setInput('locale', 'vi');
  fixture.detectChanges();
  await fixture.whenStable();
  return fixture;
}

describe('LanguageSwitchComponent', () => {
  it('associates the translated language label with the select and keeps endonym options literal', async () => {
    const fixture = await createLanguageSwitch();
    const selectElement = (fixture.nativeElement as HTMLElement).querySelector('mat-select');
    const labelledBy = selectElement?.getAttribute('aria-labelledby') ?? '';
    expect(labelledBy).not.toBe('');
    expect(
      labelledBy.split(/\s+/u).some((id) => document.getElementById(id)?.textContent?.trim() === 'Language'),
    ).toBe(true);

    const select = fixture.debugElement.query(By.directive(MatSelect)).componentInstance as MatSelect;
    select.open();
    await fixture.whenStable();
    const options = [...document.querySelectorAll<HTMLElement>('mat-option')].map((option) => option.textContent?.trim());
    expect(options).toEqual(['Tiếng Việt', 'English']);
  });

  it('emits one localeChange for a new selection and ignores the current locale', async () => {
    const fixture = await createLanguageSwitch();
    let emissions = 0;
    let selected = '';
    onEmit(fixture.componentInstance.localeChange, (locale) => {
      emissions++;
      selected = locale;
    });
    const select = fixture.debugElement.query(By.directive(MatSelect)).componentInstance as MatSelect;

    select.valueChange.emit('en');
    await fixture.whenStable();
    expect({ emissions, selected }).toEqual({ emissions: 1, selected: 'en' });

    select.valueChange.emit('vi');
    await fixture.whenStable();
    expect(emissions).toBe(1);
  });
});
