import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { FormControl, FormGroup } from '@angular/forms';
import { provideTranslocoScope } from '@jsverse/transloco';
import { describe, expect, it } from 'vitest';
import { provideAppI18n } from '@core/i18n';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import authVi from '../../../../../assets/i18n/auth/vi.json';
import coreVi from '../../../../../assets/i18n/core/vi.json';
import errorsVi from '../../../../../assets/i18n/errors/vi.json';
import { LoginFormComponent, type LoginFormControls } from '../../ui/login-form/login-form.component';

/**
 * Regression: the form's own `errors` scope used to shadow the route-level `auth` scope (the pipe resolves the
 * NEAREST `TRANSLOCO_SCOPE`), so `auth.*` keys rendered raw in a real browser while a pre-loaded testing module hid it.
 * Here the real loader and the real app config are used, with `auth` provided only above the component (as the lazy
 * route does), and the HTTP layer is the only stub.
 */
describe('LoginFormComponent scope wiring', () => {
  it('loads the auth scope next to errors and renders auth copy instead of raw keys', async () => {
    TestBed.configureTestingModule({
      providers: [
        ...ZONELESS_TEST_PROVIDERS,
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAppI18n(),
        provideTranslocoScope('auth'),
      ],
    });
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(LoginFormComponent);
    fixture.componentRef.setInput(
      'form',
      new FormGroup<LoginFormControls>({
        email: new FormControl('', { nonNullable: true }),
        password: new FormControl('', { nonNullable: true }),
      }),
    );
    fixture.detectChanges();

    http.expectOne('/assets/i18n/core/vi.json').flush(coreVi);
    http.expectOne('/assets/i18n/auth/vi.json').flush(authVi);
    http.expectOne('/assets/i18n/errors/vi.json').flush(errorsVi);
    await fixture.whenStable();
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain(authVi.login.title);
    expect(text).not.toContain('auth.login.');
    http.verify();
  });
});
