import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { SessionStore } from '@core/session';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { LegalAcceptanceRepository } from '../data-access/legal-acceptance.repository';
import type { LegalAcceptance } from '../domain/legal-acceptance.model';
import { LegalAcceptanceStore } from './legal-acceptance.store';

/** QA probes for 13.2.1-T1 (T3): session isolation, re-entry, hostile error bodies. */

const ACCEPTED_BY_A: LegalAcceptance = {
  documents: [
    { document: 'tos', currentVersion: '1.0.0', acceptedVersion: '1.0.0', acceptedAt: '2026-10-05T08:00:00Z' },
    { document: 'privacy', currentVersion: '1.0.0', acceptedVersion: '1.0.0', acceptedAt: '2026-10-05T08:00:00Z' },
  ],
};

const NOT_ACCEPTED: LegalAcceptance = {
  documents: [
    { document: 'tos', currentVersion: '1.0.0', acceptedVersion: null, acceptedAt: null },
    { document: 'privacy', currentVersion: '1.0.0', acceptedVersion: null, acceptedAt: null },
  ],
};

class FakeSession {
  private readonly state = signal(1);
  readonly generation = this.state.asReadonly();
  advance(): void {
    this.state.update((value) => value + 1);
  }
}

class FakeRepository {
  getCalls = 0;
  acceptCalls = 0;
  getHandler: () => Promise<LegalAcceptance> = () => Promise.resolve(NOT_ACCEPTED);
  acceptHandler: () => Promise<LegalAcceptance> = () => Promise.resolve(ACCEPTED_BY_A);
  get(): Promise<LegalAcceptance> {
    this.getCalls += 1;
    return this.getHandler();
  }
  accept(): Promise<LegalAcceptance> {
    this.acceptCalls += 1;
    return this.acceptHandler();
  }
}

function deferred<T>(): { promise: Promise<T>; resolve: (value: T) => void; reject: (reason: unknown) => void } {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((a, b) => {
    resolve = a;
    reject = b;
  });
  return { promise, resolve, reject };
}

function setup(): { store: LegalAcceptanceStore; repository: FakeRepository; session: FakeSession } {
  const repository = new FakeRepository();
  const session = new FakeSession();
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      LegalAcceptanceStore,
      { provide: LegalAcceptanceRepository, useValue: repository },
      { provide: SessionStore, useValue: session },
    ],
  });
  return { store: TestBed.inject(LegalAcceptanceStore), repository, session };
}

function problem(status: number, body: unknown): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: body });
}

afterEach(() => TestBed.resetTestingModule());

describe('LegalAcceptanceStore QA', () => {
  it('does not expose the previous user projection after the session changes', async () => {
    const { store, repository, session } = setup();
    repository.getHandler = () => Promise.resolve(ACCEPTED_BY_A);
    await store.refresh();
    expect(store.acceptance()).toEqual(ACCEPTED_BY_A);

    session.advance(); // logout A, sign in B

    // Nothing has called refresh() yet: B must not be able to read A's acceptance stamps meanwhile.
    expect(store.acceptance()).toBeNull();
    expect(store.missing()).toEqual([]);
  });

  it('discards a rejected accept and its error after logout', async () => {
    const { store, repository, session } = setup();
    await store.refresh();
    const response = deferred<LegalAcceptance>();
    repository.acceptHandler = () => response.promise;

    const accepting = store.accept('1.0.0', '1.0.0');
    session.advance();
    response.reject(problem(409, { code: 'legal_version_outdated', errors: { documents: ['tos'] } }));

    await expect(accepting).resolves.toBe(false);
    expect(store.errorKey()).toBeNull();
    expect(store.acceptance()).toBeNull();
    expect(repository.getCalls).toBe(1); // only the initial refresh: no post-409 refetch for a dead session
  });

  it('discards the post-409 refetch when the session ends while it is in flight', async () => {
    const { store, repository, session } = setup();
    // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- HttpClient rejects with HttpErrorResponse
    repository.acceptHandler = () => Promise.reject(problem(409, { code: 'legal_version_outdated' }));
    const refetch = deferred<LegalAcceptance>();
    repository.getHandler = () => refetch.promise;

    const accepting = store.accept('1.0.0', '1.0.0');
    await Promise.resolve();
    await Promise.resolve();
    session.advance();
    refetch.resolve(ACCEPTED_BY_A);
    await accepting;

    expect(store.acceptance()).toBeNull();
    expect(store.pending()).toBe(false);
  });

  it('ignores a second accept while one is in flight and sends exactly one request', async () => {
    const { store, repository } = setup();
    await store.refresh();
    const response = deferred<LegalAcceptance>();
    repository.acceptHandler = () => response.promise;

    const first = store.accept('1.0.0', '1.0.0');
    await expect(store.accept('1.0.0', '1.0.0')).resolves.toBe(false);
    response.resolve(ACCEPTED_BY_A);
    await expect(first).resolves.toBe(true);

    expect(repository.acceptCalls).toBe(1);
  });

  it('keeps the old projection and shows generic copy when the 409 refetch itself fails', async () => {
    const { store, repository } = setup();
    await store.refresh();
    // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- HttpClient rejects with HttpErrorResponse
    repository.acceptHandler = () => Promise.reject(problem(409, { code: 'legal_version_outdated' }));
    // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- HttpClient rejects with HttpErrorResponse
    repository.getHandler = () => Promise.reject(problem(500, { code: 'internal_error' }));

    await expect(store.accept('1.0.0', '1.0.0')).resolves.toBe(false);

    expect(store.acceptance()).toEqual(NOT_ACCEPTED);
    expect(store.pending()).toBe(false);
    expect(store.errorKey()).not.toBeNull();
  });

  it.each([
    [['__proto__', 'tos', 'constructor', 'TOS', 7, null, { a: 1 }], ['tos']],
    [['tos', 'tos', 'privacy'], ['privacy', 'tos']],
    [[], ['privacy', 'tos']], // an empty list carries no information: fall back to the refreshed projection (nothing accepted)
  ])('derives missing only from known document names in %j', async (documents, expected) => {
    const { store, repository } = setup();
    // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- HttpClient rejects with HttpErrorResponse
    repository.acceptHandler = () => Promise.reject(problem(409, { code: 'legal_version_outdated', errors: { documents } }));

    await store.accept('1.0.0', '1.0.0');

    expect(store.missing()).toEqual(expected);
  });

  it('never surfaces server problem text, trace ids or instructions as the error key', async () => {
    const { store, repository } = setup();
    repository.acceptHandler = () =>
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- HttpClient rejects with HttpErrorResponse
      Promise.reject(
        problem(500, { code: 'internal_error', title: '<script>alert(1)</script>', detail: 'secret', traceId: 'abc', nextActions: ['x'] }),
      );

    await store.accept('1.0.0', '1.0.0');

    expect(['account.legal.error.generic', 'errors.legal_version_outdated']).toContain(store.errorKey());
    expect(store.errorKey()).toBe('account.legal.error.generic');
  });

  it('maps a network failure (status 0) and a 401 to generic copy without throwing', async () => {
    const { store, repository } = setup();
    for (const status of [0, 401]) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- HttpClient rejects with HttpErrorResponse
      repository.acceptHandler = () => Promise.reject(problem(status, null));
      await expect(store.accept('1.0.0', '1.0.0')).resolves.toBe(false);
      expect(store.errorKey()).toBe('account.legal.error.generic');
      expect(store.pending()).toBe(false);
    }
  });
});
