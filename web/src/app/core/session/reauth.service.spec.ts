import { Injector } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import type { AppError, Result } from '@core/errors';
import type { ReauthDialogResult, ReauthDialogState } from '@shared/ui';
import { Subject } from 'rxjs';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { ReauthPort } from './reauth.port';
import { ReauthService } from './reauth.service';
import { SESSION_SCOPED } from './session-scope';
import { SessionStore } from './session-store';
import { SessionApi } from './session.api';
import { FakeReauthRepository, fakeReauthError, fakeReauthOk, type FakeReauthResult } from './testing/fake-reauth.repository';
import { FakeSessionApi, fakeJwt } from './testing/fake-session.api';

/** A hand-written `OutputEmitterRef`: the service only calls `subscribe`. */
class FakeOutput<T> {
  private readonly listeners = new Set<(value: T) => void>();

  subscribe(listener: (value: T) => void): { unsubscribe: () => void } {
    this.listeners.add(listener);
    return { unsubscribe: () => this.listeners.delete(listener) };
  }

  emit(value: T): void {
    for (const listener of [...this.listeners]) listener(value);
  }
}

/** The part of `MatDialogRef` + dialog component the service touches. */
class FakeDialogRef {
  readonly submitted = new FakeOutput<ReauthDialogResult>();
  readonly retryAfterElapsed = new FakeOutput<undefined>();
  readonly componentInstance = this;
  readonly states: ReauthDialogState[] = [];
  readonly componentRef = {
    setInput: (name: string, value: unknown): void => {
      expect(name).toBe('state');
      this.states.push(value as ReauthDialogState);
    },
  };
  closed = false;
  /** Mirrors `MatDialogRef.disableClose` (the dialog's own Escape/Cancel are blocked by the component). */
  disableClose = false;
  private readonly closedSubject = new Subject<void>();

  afterClosed(): Subject<void> {
    return this.closedSubject;
  }

  /** What Escape, the Cancel button (`mat-dialog-close`) and the service's own close all do. */
  close(): void {
    if (this.closed) return;
    this.closed = true;
    this.closedSubject.next();
    this.closedSubject.complete();
  }

  /** MatDialog closes on a backdrop click only while `disableClose` is false. */
  backdropClick(): void {
    if (!this.disableClose) this.close();
  }

  submit(password = 'pw', code = '123456'): void {
    this.submitted.emit({ password, code });
  }

  get lastState(): ReauthDialogState | undefined {
    return this.states[this.states.length - 1];
  }
}

class FakeMatDialog {
  readonly refs: FakeDialogRef[] = [];
  readonly configs: unknown[] = [];

  open(_component: unknown, config: unknown): FakeDialogRef {
    const ref = new FakeDialogRef();
    this.refs.push(ref);
    this.configs.push(config);
    return ref;
  }

  /** The ref at `index`; throws when absent (`noUncheckedIndexedAccess` makes `refs[i]` possibly undefined). */
  at(index: number): FakeDialogRef {
    const ref = this.refs[index];
    if (ref === undefined) throw new Error(`no dialog ref opened at index ${index}`);
    return ref;
  }
}

interface Harness {
  readonly service: ReauthService;
  readonly store: SessionStore;
  readonly repo: FakeReauthRepository;
  readonly dialog: FakeMatDialog;
  readonly sessionApi: FakeSessionApi;
}

function ownerTokens(sub = 'owner-1'): { accessToken: string; expiresAt: number } {
  return { accessToken: fakeJwt('owner', ['pwd', 'otp'], sub), expiresAt: Date.now() + 15 * 60_000 };
}

function setup(signedIn = true): Harness {
  const repo = new FakeReauthRepository();
  const sessionApi = new FakeSessionApi();
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([{ path: '**', children: [] }]),
      { provide: SessionApi, useValue: sessionApi },
      { provide: ReauthPort, useValue: repo },
      { provide: MatDialog, useClass: FakeMatDialog },
      {
        // The same lazy entry `provideSession()` registers (a direct reference would be a cycle).
        provide: SESSION_SCOPED,
        multi: true,
        useFactory: () => {
          const injector = TestBed.inject(Injector);
          return { reset: (): void => injector.get(ReauthService).reset() };
        },
      },
    ],
  });
  const store = TestBed.inject(SessionStore);
  if (signedIn) store.signIn(ownerTokens(), 'owner', ['pwd', 'otp']);
  return {
    service: TestBed.inject(ReauthService),
    store,
    repo,
    dialog: TestBed.inject(MatDialog) as unknown as FakeMatDialog,
    sessionApi,
  };
}

const flush = (): Promise<void> => new Promise((resolve) => setTimeout(resolve));

/** The dialog module loads lazily on first open: poll until `MatDialog.open` produced `count` refs. */
function opened(dialog: FakeMatDialog, count = 1): Promise<void> {
  return vi.waitFor(() => expect(dialog.refs).toHaveLength(count), { timeout: 2_000, interval: 1 });
}

const REAUTH_REQUIRED: Result<never> = {
  ok: false,
  error: { code: 'reauth_required', status: 401, kind: 'auth', fieldErrors: {} },
};

function errorOf(result: Result<unknown>): AppError {
  if (result.ok) throw new Error('expected an err result');
  return result.error;
}

describe('ReauthService', () => {
  it('ReauthService_Open_UsesFocusSafeDialogConfigAndRequiresCode', async () => {
    const { service, dialog } = setup();
    void service.ensureReauthenticated();
    await opened(dialog);

    expect(dialog.configs).toEqual([{ data: { requireCode: true }, restoreFocus: true, autoFocus: '#reauth-password', ariaModal: true }]);
    expect(service.isDialogOpen()).toBe(true);
  });

  it('ReauthService_ConcurrentCallers_ShareOneDialogOnePostAndBothResolve', async () => {
    const { service, dialog, repo } = setup();
    const first = service.ensureReauthenticated();
    // The flight registers before the lazy import resolves: a second caller joins the SAME promise synchronously.
    const second = service.ensureReauthenticated();
    expect(second).toBe(first);
    await opened(dialog);
    expect(dialog.refs).toHaveLength(1);

    dialog.at(0).submit('pw', '123456');

    await expect(first).resolves.toEqual({ ok: true, value: undefined });
    await expect(second).resolves.toEqual({ ok: true, value: undefined });
    expect(repo.calls).toEqual([{ password: 'pw', code: '123456' }]);
    expect(dialog.at(0).closed).toBe(true);
    expect(service.isDialogOpen()).toBe(false);
  });

  it('ReauthService_DoubleSubmitWhileInFlight_IssuesOnePost', async () => {
    const { service, dialog, repo } = setup();
    let release!: (result: FakeReauthResult) => void;
    repo.enqueue(new Promise<FakeReauthResult>((resolve) => (release = resolve)));
    const pending = service.ensureReauthenticated();
    await opened(dialog);

    dialog.at(0).submit();
    dialog.at(0).submit();
    expect(repo.calls).toHaveLength(1);
    expect(dialog.at(0).lastState).toMatchObject({ submitting: true });

    release(fakeReauthOk());
    await expect(pending).resolves.toMatchObject({ ok: true });
  });

  it('ReauthService_Submit_SpacedCode_SendsCompactDigits', async () => {
    const { service, dialog, repo } = setup();
    repo.enqueue(Promise.resolve(fakeReauthOk()));
    const pending = service.ensureReauthenticated();
    await opened(dialog);

    dialog.at(0).submit('pw', '123 456');
    await expect(pending).resolves.toMatchObject({ ok: true });
    expect(repo.calls).toHaveLength(1);
    expect(JSON.stringify(repo.calls[0])).toContain('123456');
    expect(JSON.stringify(repo.calls[0])).not.toContain('123 456');
  });

  it('ReauthService_Submit_WhitespaceOnlyCode_SendsNothingAndKeepsDialogOpen', async () => {
    const { service, dialog, repo } = setup();
    const pending = service.ensureReauthenticated();
    await opened(dialog);

    dialog.at(0).submit('pw', '   ');
    expect(repo.calls).toHaveLength(0);
    expect(dialog.at(0).lastState).not.toMatchObject({ submitting: true });

    dialog.at(0).close();
    await expect(pending).resolves.toMatchObject({ ok: false });
  });

  it('ReauthService_WithReauth_Ok_RetriesActionOnceAndWritesNoClientState', async () => {
    const { service, store, dialog, repo, sessionApi } = setup();
    const token = store.accessToken();
    const generation = store.generation();
    let calls = 0;
    const action = (): Promise<Result<string>> => {
      calls += 1;
      return Promise.resolve(calls === 1 ? REAUTH_REQUIRED : { ok: true, value: 'done' });
    };

    const result = service.withReauth(action);
    await opened(dialog);
    expect(calls).toBe(1);
    dialog.at(0).submit();

    await expect(result).resolves.toEqual({ ok: true, value: 'done' });
    expect(calls).toBe(2);
    expect(repo.calls).toHaveLength(1);
    // [WR8] the client keeps nothing: same token, same session, no refresh.
    expect(store.accessToken()).toBe(token);
    expect(store.generation()).toBe(generation);
    expect(sessionApi.calls).toBe(0);
  });

  it('ReauthService_WithReauth_ActionStillReauthRequired_ReturnsItAfterExactlyOneRetry', async () => {
    const { service, dialog } = setup();
    let calls = 0;
    const result = service.withReauth(() => {
      calls += 1;
      return Promise.resolve(REAUTH_REQUIRED);
    });
    await opened(dialog);
    dialog.at(0).submit();

    expect(errorOf(await result).code).toBe('reauth_required');
    expect(calls).toBe(2);
    expect(dialog.refs).toHaveLength(1);
  });

  it('ReauthService_WithReauth_OtherOutcomes_NeverOpenTheDialog', async () => {
    const { service, dialog } = setup();
    await expect(service.withReauth(() => Promise.resolve({ ok: true, value: 1 } as Result<number>))).resolves.toEqual({
      ok: true,
      value: 1,
    });
    const conflict: Result<number> = { ok: false, error: { code: 'concurrency_conflict', status: 412, kind: 'conflict', fieldErrors: {} } };
    await expect(service.withReauth(() => Promise.resolve(conflict))).resolves.toBe(conflict);
    expect(dialog.refs).toHaveLength(0);
  });

  it('ReauthService_WithReauth_Cancelled_DoesNotRetryTheAction', async () => {
    const { service, dialog } = setup();
    let calls = 0;
    const result = service.withReauth(() => {
      calls += 1;
      return Promise.resolve(REAUTH_REQUIRED);
    });
    await opened(dialog);
    dialog.at(0).close();

    expect(errorOf(await result)).toMatchObject({ code: 'reauth_required', status: 0 });
    expect(calls).toBe(1);
  });

  it('ReauthService_WithReauth_SessionReplacedDuringDialog_DoesNotReplayUnderTheNewSession', async () => {
    const { service, store, dialog } = setup();
    let calls = 0;
    const result = service.withReauth(() => {
      calls += 1;
      return Promise.resolve(REAUTH_REQUIRED);
    });
    await opened(dialog);
    store.signIn(ownerTokens('owner-2'), 'owner', ['pwd', 'otp']);
    TestBed.tick();

    expect(errorOf(await result)).toMatchObject({ code: 'session_revoked', status: 0 });
    expect(calls).toBe(1);
    expect(dialog.at(0).closed).toBe(true);
  });

  it.each([
    ['invalid_credentials', 'password'],
    ['invalid_mfa_code', 'code'],
  ] as const)('ReauthService_%s_ShowsFieldErrorKeepsDialogOpenThenRetrySucceeds', async (code, field) => {
    const { service, dialog, repo } = setup();
    repo.enqueue(fakeReauthError(code, 401, 'auth'));
    const settled = vi.fn();
    const pending = service.ensureReauthenticated();
    void pending.then(settled);
    await opened(dialog);

    dialog.at(0).submit();
    await flush();

    expect(dialog.at(0).states[0]).toMatchObject({ submitting: true });
    expect(dialog.at(0).lastState).toEqual({
      submitting: false,
      errorKey: `errors.${code}`,
      errorField: field,
      traceId: null,
      retryAfterSec: null,
    });
    expect(dialog.at(0).closed).toBe(false);
    expect(service.isDialogOpen()).toBe(true);
    expect(settled).not.toHaveBeenCalled();

    dialog.at(0).submit('pw2', '654321');
    await expect(pending).resolves.toMatchObject({ ok: true });
    expect(repo.calls).toHaveLength(2);
  });

  it('ReauthService_SessionRevoked_ClosesDialogAndResolvesErr', async () => {
    const { service, dialog, repo } = setup();
    repo.enqueue(fakeReauthError('session_revoked', 401, 'auth'));
    const pending = service.ensureReauthenticated();
    await opened(dialog);

    dialog.at(0).submit();

    expect(errorOf(await pending)).toMatchObject({ code: 'session_revoked', status: 401 });
    expect(dialog.at(0).closed).toBe(true);
    expect(service.isDialogOpen()).toBe(false);
  });

  it('ReauthService_RateLimited_ShowsRetryAfterDisablesSubmitUntilElapsedThenSecondAttemptWorks', async () => {
    const { service, dialog, repo } = setup();
    repo.enqueue(fakeReauthError('rate_limited', 429, 'rate_limit', 30));
    const pending = service.ensureReauthenticated();
    await opened(dialog);
    const ref = dialog.at(0);

    ref.submit();
    await flush();
    expect(ref.lastState).toMatchObject({ submitting: false, errorKey: 'errors.rate_limited', errorField: null, retryAfterSec: 30 });

    ref.submit(); // the button is disabled while the countdown runs: no second POST
    await flush();
    expect(repo.calls).toHaveLength(1);

    ref.retryAfterElapsed.emit(undefined);
    expect(ref.lastState).toEqual({ submitting: false, errorKey: null, errorField: null, traceId: null, retryAfterSec: null });

    ref.submit();
    await expect(pending).resolves.toMatchObject({ ok: true });
    expect(repo.calls).toHaveLength(2);
  });

  it('ReauthService_NetworkFailure_ShowsFormLevelFailedKeyAndStaysOpen', async () => {
    const { service, dialog, repo } = setup();
    repo.enqueue(fakeReauthError('network_error', 0, 'network'));
    void service.ensureReauthenticated();
    await opened(dialog);

    dialog.at(0).submit();
    await flush();

    expect(dialog.at(0).lastState).toMatchObject({ errorKey: 'reauth.failed', errorField: null, submitting: false });
    expect(dialog.at(0).closed).toBe(false);
  });

  it('ReauthService_Logout_ClosesDialogAndRejectsEveryWaiter', async () => {
    const { service, store, dialog } = setup();
    const first = service.ensureReauthenticated();
    const second = service.ensureReauthenticated();
    await opened(dialog);

    store.signOut('logout');

    expect(errorOf(await first)).toMatchObject({ code: 'session_revoked', status: 0 });
    expect(errorOf(await second)).toMatchObject({ code: 'session_revoked', status: 0 });
    expect(dialog.at(0).closed).toBe(true);
    expect(service.isDialogOpen()).toBe(false);
  });

  it('ReauthService_LateOkAfterLogout_IsIgnored', async () => {
    const { service, store, dialog, repo } = setup();
    let release!: (result: FakeReauthResult) => void;
    repo.enqueue(new Promise<FakeReauthResult>((resolve) => (release = resolve)));
    const pending = service.ensureReauthenticated();
    await opened(dialog);
    const ref = dialog.at(0);
    ref.submit();
    const painted = ref.states.length;

    store.signOut('logout');
    release(fakeReauthOk());

    expect(errorOf(await pending)).toMatchObject({ code: 'session_revoked', status: 0 });
    await flush();
    expect(ref.states).toHaveLength(painted);
    expect(service.isDialogOpen()).toBe(false);
  });

  it('ReauthService_LateOkAfterSignInAsAnotherUser_NeverResolvesOk', async () => {
    const { service, store, dialog, repo } = setup();
    let release!: (result: FakeReauthResult) => void;
    repo.enqueue(new Promise<FakeReauthResult>((resolve) => (release = resolve)));
    const pending = service.ensureReauthenticated();
    await opened(dialog);
    dialog.at(0).submit();

    store.signIn(ownerTokens('owner-2'), 'owner', ['pwd', 'otp']);
    release(fakeReauthOk()); // answered before the effect ran: the generation check still ends it

    expect(errorOf(await pending)).toMatchObject({ code: 'session_revoked', status: 0 });
    expect(dialog.at(0).closed).toBe(true);
  });

  it('ReauthService_AfterGenerationChange_NextCallerOpensANewDialog', async () => {
    const { service, store, dialog } = setup();
    const old = service.ensureReauthenticated();
    await opened(dialog);
    store.signIn(ownerTokens('owner-2'), 'owner', ['pwd', 'otp']);

    const fresh = service.ensureReauthenticated();
    await opened(dialog, 2);
    expect(errorOf(await old).status).toBe(0);

    dialog.at(1).submit();
    await expect(fresh).resolves.toMatchObject({ ok: true });
  });

  it('ReauthService_DialogClosedWithoutSuccess_ResolvesCancelledAndIssuesNoPost', async () => {
    const { service, dialog, repo } = setup();
    const pending = service.ensureReauthenticated();
    await opened(dialog);

    dialog.at(0).close();

    expect(errorOf(await pending)).toEqual({ code: 'reauth_required', status: 0, kind: 'auth', fieldErrors: {} });
    expect(repo.calls).toHaveLength(0);
    expect(service.isDialogOpen()).toBe(false);
  });

  it('ReauthService_CancelWhilePostInFlight_IgnoresTheLateAnswer', async () => {
    const { service, dialog, repo } = setup();
    let release!: (result: FakeReauthResult) => void;
    repo.enqueue(new Promise<FakeReauthResult>((resolve) => (release = resolve)));
    const pending = service.ensureReauthenticated();
    await opened(dialog);
    dialog.at(0).submit();

    dialog.at(0).close();
    release(fakeReauthOk());

    expect(errorOf(await pending)).toMatchObject({ code: 'reauth_required', status: 0 });
  });

  it('ReauthService_SessionReplacedWhileDialogModuleLoads_OpensNoDialog', async () => {
    const { service, store, dialog } = setup();
    const pending = service.ensureReauthenticated();
    // The flight is registered but the lazy import has not resolved: sign-in bumps the generation underneath it.
    store.signIn(ownerTokens('owner-2'), 'owner', ['pwd', 'otp']);

    expect(errorOf(await pending)).toMatchObject({ code: 'session_revoked', status: 0 });
    await flush(); // let the import resolve: it must never open a dialog for the ended flight
    expect(dialog.refs).toHaveLength(0);
    expect(service.isDialogOpen()).toBe(false);
  });

  it('ReauthService_LogoutWhileDialogModuleLoads_OpensNoDialog', async () => {
    const { service, store, dialog } = setup();
    const pending = service.ensureReauthenticated();

    store.signOut('logout');

    expect(errorOf(await pending)).toMatchObject({ code: 'session_revoked', status: 0 });
    await flush();
    expect(dialog.refs).toHaveLength(0);
    expect(service.isDialogOpen()).toBe(false);
  });

  it('ReauthService_DialogOpenThrows_ResolvesErrAndNextCallOpensAFreshFlight', async () => {
    const { service, dialog } = setup();
    vi.spyOn(dialog, 'open').mockImplementationOnce(() => {
      throw new Error('dialog.open failed');
    });

    // A synchronous `open` throw must settle the registered flight — the same catalogue error a failed chunk
    // load produces — instead of leaving every later caller joined to a promise that never resolves.
    const first = service.ensureReauthenticated();
    expect(errorOf(await first)).toEqual({ code: 'network_error', status: 0, kind: 'network', fieldErrors: {} });
    expect(service.isDialogOpen()).toBe(false);

    // The dead flight is gone: the next caller registers a fresh flight and a real dialog opens.
    const second = service.ensureReauthenticated();
    await opened(dialog);
    expect(dialog.refs).toHaveLength(1);

    dialog.at(0).submit();
    await expect(second).resolves.toMatchObject({ ok: true });
  });

  it('ReauthService_SubmitBlocksBackdropWhilePostInFlight_AndRestoresAfter', async () => {
    const { service, dialog, repo } = setup();
    let release!: (result: FakeReauthResult) => void;
    repo.enqueue(new Promise<FakeReauthResult>((resolve) => (release = resolve)));
    const pending = service.ensureReauthenticated();
    await opened(dialog);
    const ref = dialog.at(0);

    ref.submit();
    expect(ref.disableClose).toBe(true);
    ref.backdropClick(); // Escape/Cancel are blocked too: the third dismiss path must not close mid-POST
    expect(ref.closed).toBe(false);

    release(fakeReauthError('invalid_credentials', 401, 'auth'));
    await flush();
    expect(ref.disableClose).toBe(false);

    ref.backdropClick();
    expect(ref.closed).toBe(true);
    expect(errorOf(await pending)).toMatchObject({ code: 'reauth_required', status: 0 });
  });

  it('ReauthService_WithReauth_GenerationChangedByFirstAction_EndsWithoutOpening', async () => {
    const { service, store, dialog } = setup();
    let calls = 0;
    const result = service.withReauth(() => {
      calls += 1;
      if (calls === 1) store.signIn(ownerTokens('owner-2'), 'owner', ['pwd', 'otp']); // session replaced mid-action
      return Promise.resolve(REAUTH_REQUIRED);
    });

    expect(errorOf(await result)).toMatchObject({ code: 'session_revoked', status: 0 });
    await flush();
    expect(calls).toBe(1);
    expect(dialog.refs).toHaveLength(0);
  });

  it('ReauthService_NoFullSession_ResolvesErrWithoutOpeningADialog', async () => {
    const { service, store, dialog } = setup(false);
    expect(errorOf(await service.ensureReauthenticated())).toMatchObject({ code: 'session_revoked', status: 0 });

    store.beginEnrolment('enrolment-token');
    expect(errorOf(await service.ensureReauthenticated())).toMatchObject({ code: 'session_revoked', status: 0 });
    expect(dialog.refs).toHaveLength(0);
  });
});
