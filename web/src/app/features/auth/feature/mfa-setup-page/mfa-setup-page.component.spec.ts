import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { TranslocoTestingModule, provideTranslocoScope } from '@jsverse/transloco';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AppError, Result } from '@core/errors';
import { ReauthService, SessionApi, SessionStore } from '@core/session';
import { FakeSessionApi, fakeJwt } from '../../../../core/session/testing/fake-session.api';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import authEn from '../../../../../assets/i18n/auth/en.json';
import coreEn from '../../../../../assets/i18n/core/en.json';
import errorsEn from '../../../../../assets/i18n/errors/en.json';
import { EnrolmentStore } from '../../application/enrolment.store';
import { MfaRepository } from '../../application/mfa.repository';
import { FakeMfaRepository, fakeRecoveryCodes } from '../../testing/fake-mfa.repository';
import { MfaSetupPageComponent } from './mfa-setup-page.component';

const translations = TranslocoTestingModule.forRoot({
  langs: { en: coreEn, 'auth/en': authEn, 'errors/en': errorsEn },
  translocoConfig: { availableLangs: ['en'], defaultLang: 'en', fallbackLang: 'en' },
  preloadLangs: true,
});

/** Hand-written `ReauthService` stand-in (D15): the dialog is never asked for here, actions just run. */
class FakeReauthService {
  withReauth<T>(action: () => Promise<Result<T>>): Promise<Result<T>> {
    return action();
  }
  ensureReauthenticated(): Promise<Result<void>> {
    return Promise.resolve({ ok: true, value: undefined });
  }
}

async function setup(options: { enrolmentOnly?: boolean } = {}) {
  const enrolmentOnly = options.enrolmentOnly ?? true;
  const repo = new FakeMfaRepository();
  await TestBed.configureTestingModule({
    imports: [MfaSetupPageComponent, translations],
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([]),
      provideTranslocoScope('auth'),
      { provide: SessionApi, useValue: new FakeSessionApi() },
      { provide: MfaRepository, useValue: repo },
      { provide: ReauthService, useClass: FakeReauthService },
    ],
  }).compileComponents();
  const session = TestBed.inject(SessionStore);
  if (enrolmentOnly) session.beginEnrolment('enrolment-token');
  else session.signIn({ accessToken: fakeJwt('owner', ['pwd', 'otp']), expiresAt: Date.now() + 900_000 }, 'owner', ['pwd', 'otp']);
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(MfaSetupPageComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  const store = fixture.debugElement.injector.get(EnrolmentStore);
  const page = fixture.componentInstance as unknown as {
    verifyCode(): void;
    finish(): void;
    download(): void;
    confirmSavedChange(value: boolean): void;
    reLogin(): void;
  };
  return { fixture, repo, store, page, session, navigate, root: fixture.nativeElement as HTMLElement };
}

async function settle(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }): Promise<void> {
  await fixture.whenStable();
  fixture.detectChanges();
  await fixture.whenStable();
}

afterEach(() => {
  TestBed.resetTestingModule();
  vi.restoreAllMocks();
});

describe('MfaSetupPageComponent', () => {
  it('MfaSetupPage_Created_LoadsSetupOnceAndRendersTheSetupView', async () => {
    const { repo, store, root } = await setup();
    expect(repo.callsOf('setup')).toBe(1);
    expect(store.step()).toBe('show');
    expect(root.querySelector('nts-mfa-setup-view')).not.toBeNull();
  });

  it('MfaSetupPage_Secret_NeverReflectedIntoAnAttributeOrStorage', async () => {
    const setItem = vi.spyOn(Storage.prototype, 'setItem');
    const { fixture, store, root } = await setup();
    await settle(fixture);
    const secret = store.setup()?.secret ?? '';
    expect(secret).not.toBe('');
    expect([...root.querySelectorAll('*')].some((node) => [...node.attributes].some((attr) => attr.value.includes(secret)))).toBe(false);
    expect(setItem).not.toHaveBeenCalled();
  });

  it('MfaSetupPage_EmptyCode_MakesNoVerifyRequest', async () => {
    const { fixture, repo, page } = await setup();
    page.verifyCode();
    await settle(fixture);
    expect(repo.callsOf('verify')).toBe(0);
  });

  it('MfaSetupPage_ValidCode_RevealsCodesAndFieldIsEmptied', async () => {
    const { fixture, repo, store, page } = await setup();
    fixture.componentInstance.form.controls.code.setValue('123456');
    page.verifyCode();
    await settle(fixture);
    expect(repo.calls.filter((call) => call.method === 'verify')).toEqual([{ method: 'verify', args: ['123456'] }]);
    expect(store.step()).toBe('codes');
    expect(store.codes()).toHaveLength(10);
    expect(fixture.componentInstance.form.controls.code.value).toBe('');
  });

  it('MfaSetupPage_InvalidCode_BindsAServerErrorToTheCodeControlAndStaysOnVerify', async () => {
    const { fixture, repo, store, page } = await setup();
    const wrong: AppError = { code: 'invalid_mfa_code', status: 401, kind: 'auth', fieldErrors: {} };
    repo.willFail('verify', wrong);
    fixture.componentInstance.form.controls.code.setValue('000000');
    page.verifyCode();
    await settle(fixture);
    expect(store.step()).toBe('verify');
    expect(fixture.componentInstance.form.controls.code.errors?.['server']).toEqual({ messageKeys: ['errors.invalid_mfa_code'] });
  });

  it('MfaSetupPage_WrongCodeThenUserEdit_SubmitsTheCorrectedCodeAndRevealsCodes', async () => {
    const { fixture, repo, store, root } = await setup();
    const wrong: AppError = { code: 'invalid_mfa_code', status: 401, kind: 'auth', fieldErrors: {} };
    repo.willFail('verify', wrong).willReturn('verify', { ok: true, value: fakeRecoveryCodes() });
    await settle(fixture);
    const code = root.querySelector<HTMLInputElement>('[data-code-field]')!;
    const form = root.querySelector('form')!;
    code.value = '000000';
    code.dispatchEvent(new Event('input', { bubbles: true }));
    form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await settle(fixture);
    expect(fixture.componentInstance.form.controls.code.errors?.['server']).toBeDefined();

    code.value = '123456';
    code.dispatchEvent(new Event('input', { bubbles: true }));
    // No render/effect turn between editing and submitting: server feedback is cleared synchronously.
    expect(fixture.componentInstance.form.controls.code.errors?.['server']).toBeUndefined();
    expect(fixture.componentInstance.form.valid).toBe(true);
    form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await settle(fixture);
    expect(repo.calls.filter((call) => call.method === 'verify')).toEqual([
      { method: 'verify', args: ['000000'] },
      { method: 'verify', args: ['123456'] },
    ]);
    expect(store.step()).toBe('codes');
    expect(store.codes()).toHaveLength(10);
  });

  it('MfaSetupPage_WrongCodeThenEmptyEdit_KeepsRequiredValidationAndDoesNotPost', async () => {
    const { fixture, repo, page } = await setup();
    repo.willFail('verify', { code: 'invalid_mfa_code', status: 401, kind: 'auth', fieldErrors: {} });
    fixture.componentInstance.form.controls.code.setValue('000000');
    page.verifyCode();
    await settle(fixture);
    fixture.componentInstance.form.controls.code.setValue('');
    expect(fixture.componentInstance.form.controls.code.errors?.['server']).toBeUndefined();
    expect(fixture.componentInstance.form.controls.code.errors?.['required']).toBe(true);
    page.verifyCode();
    expect(repo.callsOf('verify')).toBe(1);
  });

  it('MfaSetupPage_StepRenderWhileDialogOpen_DoesNotStealDialogFocus', async () => {
    const dialog = document.createElement('div');
    dialog.setAttribute('role', 'dialog');
    const password = document.createElement('input');
    dialog.append(password);
    document.body.append(dialog);
    password.focus();
    try {
      const { fixture, root } = await setup();
      await settle(fixture);
      const heading = root.querySelector<HTMLElement>('[data-page-heading]')!;
      const focus = vi.spyOn(heading, 'focus');
      fixture.componentInstance.form.controls.code.setValue('123456');
      (fixture.componentInstance as unknown as { verifyCode(): void }).verifyCode();
      await settle(fixture);
      expect(focus).not.toHaveBeenCalled();
      expect(document.activeElement).toBe(password);
    } finally {
      dialog.remove();
    }
  });

  describe('download', () => {
    it('MfaSetupPage_Download_SavesTheTenCodesAsDatedTxtThroughTheSharedPath', async () => {
      const blobs: Blob[] = [];
      Object.assign(URL, {
        createObjectURL: vi.fn((blob: Blob) => {
          blobs.push(blob);
          return 'blob:codes';
        }),
        revokeObjectURL: vi.fn(),
      });
      const names: string[] = [];
      vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
        names.push(this.download);
      });
      const { fixture, repo, page } = await setup();
      const codes = fakeRecoveryCodes('rc');
      repo.willReturn('verify', { ok: true, value: codes });
      fixture.componentInstance.form.controls.code.setValue('123456');
      page.verifyCode();
      await settle(fixture);
      page.download();
      expect(names).toHaveLength(1);
      expect(names[0]).toMatch(/^recovery-codes-\d{8}\.txt$/);
      expect(blobs).toHaveLength(1);
      expect(blobs[0]?.type).toBe('text/plain');
      const text = await blobs[0]!.text();
      for (const code of codes) expect(text).toContain(code);
    });

    it('MfaSetupPage_Download_WithoutCodes_IsANoOp', async () => {
      const createObjectURL = vi.fn(() => 'blob:x');
      Object.assign(URL, { createObjectURL, revokeObjectURL: vi.fn() });
      const { page } = await setup();
      page.download();
      expect(createObjectURL).not.toHaveBeenCalled();
    });
  });

  describe('finish', () => {
    async function atCodes(options: { enrolmentOnly: boolean }) {
      const ctx = await setup(options);
      ctx.fixture.componentInstance.form.controls.code.setValue('123456');
      ctx.page.verifyCode();
      await settle(ctx.fixture);
      return ctx;
    }

    it('MfaSetupPage_Finish_WithoutConfirmedSaved_DoesNothing', async () => {
      const { store, page, navigate } = await atCodes({ enrolmentOnly: false });
      page.finish();
      expect(store.step()).toBe('codes');
      expect(store.codes()).toHaveLength(10);
      expect(navigate).not.toHaveBeenCalled();
    });

    it('MfaSetupPage_Finish_FullSession_DropsCodesAndGoesToDashboard', async () => {
      const { store, page, navigate } = await atCodes({ enrolmentOnly: false });
      page.confirmSavedChange(true);
      page.finish();
      expect(store.codes()).toBeNull();
      expect(store.step()).toBe('done');
      expect(navigate).toHaveBeenCalledWith('/dashboard');
    });

    it('MfaSetupPage_Finish_EnrolmentOnly_StaysOnDoneAndReLoginClearsTheTokenThenGoesToLogin', async () => {
      const { store, page, navigate, session } = await atCodes({ enrolmentOnly: true });
      page.confirmSavedChange(true);
      page.finish();
      expect(store.step()).toBe('done');
      expect(navigate).not.toHaveBeenCalled();
      page.reLogin();
      await Promise.resolve();
      expect(session.enrolmentToken()).toBeNull();
      expect(navigate).toHaveBeenCalledWith('/login');
    });
  });

  it('MfaSetupPage_Destroy_DropsTheSecretAndTheCodes', async () => {
    const { fixture, store, page } = await setup();
    expect(store.setup()).not.toBeNull();
    fixture.componentInstance.form.controls.code.setValue('123456');
    page.verifyCode();
    await settle(fixture);
    expect(store.codes()).toHaveLength(10);
    fixture.destroy();
    expect(store.setup()).toBeNull();
    expect(store.codes()).toBeNull();
  });
});
