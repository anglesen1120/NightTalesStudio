import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { SessionStore } from '@core/session';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { LegalAcceptanceRepository } from '../data-access/legal-acceptance.repository';
import type { LegalAcceptance } from '../domain/legal-acceptance.model';
import { LegalAcceptanceStore } from './legal-acceptance.store';

const NOT_ACCEPTED: LegalAcceptance = {
  documents: [
    { document: 'tos', currentVersion: '1.0.0', acceptedVersion: null, acceptedAt: null },
    { document: 'privacy', currentVersion: '1.0.0', acceptedVersion: null, acceptedAt: null },
  ],
};

const ACCEPTED: LegalAcceptance = {
  documents: [
    { document: 'tos', currentVersion: '1.0.0', acceptedVersion: '1.0.0', acceptedAt: '2026-10-05T08:00:00Z' },
    { document: 'privacy', currentVersion: '1.0.0', acceptedVersion: '1.0.0', acceptedAt: '2026-10-05T08:00:00Z' },
  ],
};

class FakeSessionStore {
  private readonly generationState = signal(1);
  readonly generation = this.generationState.asReadonly();

  advance(): void {
    this.generationState.update((value) => value + 1);
  }
}

class FakeLegalAcceptanceRepository {
  getCalls = 0;
  readonly acceptCalls: { readonly tos: string; readonly privacy: string }[] = [];
  getHandler: () => Promise<LegalAcceptance> = () => Promise.resolve(NOT_ACCEPTED);
  acceptHandler: (tos: string, privacy: string) => Promise<LegalAcceptance> = () => Promise.resolve(ACCEPTED);

  get(): Promise<LegalAcceptance> {
    this.getCalls += 1;
    return this.getHandler();
  }

  accept(tos: string, privacy: string): Promise<LegalAcceptance> {
    this.acceptCalls.push({ tos, privacy });
    return this.acceptHandler(tos, privacy);
  }
}

interface Deferred<T> {
  readonly promise: Promise<T>;
  readonly resolve: (value: T) => void;
  readonly reject: (reason: unknown) => void;
}

function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

function outdated(documents?: readonly string[], extra: Readonly<Record<string, unknown>> = {}): HttpErrorResponse {
  return new HttpErrorResponse({
    status: 409,
    error: {
      code: 'legal_version_outdated',
      ...(documents === undefined ? {} : { errors: { documents } }),
      ...extra,
    },
  });
}

function setup(): {
  readonly store: LegalAcceptanceStore;
  readonly repository: FakeLegalAcceptanceRepository;
  readonly session: FakeSessionStore;
} {
  const repository = new FakeLegalAcceptanceRepository();
  const session = new FakeSessionStore();
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

afterEach(() => TestBed.resetTestingModule());

describe('LegalAcceptanceStore', () => {
  it('refreshes the projection and computes missing documents in alphabetical wire order', async () => {
    const { store, repository } = setup();
    repository.getHandler = () => Promise.resolve(NOT_ACCEPTED);

    await expect(store.refresh()).resolves.toBe(true);

    expect(repository.getCalls).toBe(1);
    expect(store.acceptance()).toEqual(NOT_ACCEPTED);
    expect(store.missing()).toEqual(['privacy', 'tos']);
    expect(store.pending()).toBe(false);
  });

  it('keeps the projection unchanged until accept returns 200 and sends the supplied rendered versions', async () => {
    const { store, repository } = setup();
    await store.refresh();
    const response = deferred<LegalAcceptance>();
    repository.acceptHandler = () => response.promise;

    const accepting = store.accept('1.0.0', '1.0.0');
    expect(store.pending()).toBe(true);
    expect(store.acceptance()).toEqual(NOT_ACCEPTED);
    expect(repository.acceptCalls).toEqual([{ tos: '1.0.0', privacy: '1.0.0' }]);

    response.resolve(ACCEPTED);
    await expect(accepting).resolves.toBe(true);
    expect(store.acceptance()).toEqual(ACCEPTED);
    expect(store.missing()).toEqual([]);
  });

  it('maps legal_version_outdated, uses fieldErrors.documents and refetches the authoritative projection', async () => {
    const { store, repository } = setup();
    // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- HttpClient rejects with HttpErrorResponse, not Error
    repository.acceptHandler = () => Promise.reject(outdated(['tos', 'privacy'], { nextActions: ['ignore-contract'] }));
    repository.getHandler = () => Promise.resolve({
      documents: [
        { document: 'tos', currentVersion: '1.1.0', acceptedVersion: '1.0.0', acceptedAt: '2026-10-01T08:00:00Z' },
        { document: 'privacy', currentVersion: '1.2.0', acceptedVersion: '1.0.0', acceptedAt: '2026-10-01T08:00:00Z' },
      ],
    });

    await expect(store.accept('1.0.0', '1.0.0')).resolves.toBe(false);

    expect(repository.getCalls).toBe(1);
    expect(store.acceptance()?.documents.map((document) => document.currentVersion)).toEqual(['1.1.0', '1.2.0']);
    expect(store.errorKey()).toBe('errors.legal_version_outdated');
    expect(store.missing()).toEqual(['privacy', 'tos']);
  });

  it('computes missing from the refreshed projection when errors.documents is absent', async () => {
    const { store, repository } = setup();
    // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- HttpClient rejects with HttpErrorResponse, not Error
    repository.acceptHandler = () => Promise.reject(outdated());
    repository.getHandler = () => Promise.resolve({
      documents: [
        { document: 'tos', currentVersion: '1.0.0', acceptedVersion: '1.0.0', acceptedAt: '2026-10-01T08:00:00Z' },
        { document: 'privacy', currentVersion: '1.1.0', acceptedVersion: '1.0.0', acceptedAt: '2026-10-01T08:00:00Z' },
      ],
    });

    await store.accept('1.0.0', '1.0.0');

    expect(store.missing()).toEqual(['privacy']);
    expect(store.errorKey()).toBe('errors.legal_version_outdated');
  });

  it('maps non-version failures to generic copy and keeps the current projection', async () => {
    const { store, repository } = setup();
    await store.refresh();
    repository.acceptHandler = () => {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- HttpClient rejects with HttpErrorResponse, not Error
      return Promise.reject(
        new HttpErrorResponse({ status: 500, error: { code: 'internal_error', detail: 'not rendered' } }),
      );
    };

    await expect(store.accept('1.0.0', '1.0.0')).resolves.toBe(false);

    expect(store.acceptance()).toEqual(NOT_ACCEPTED);
    expect(store.errorKey()).toBe('account.legal.error.generic');
  });

  it('discards a late refresh after the session generation changes', async () => {
    const { store, repository, session } = setup();
    const response = deferred<LegalAcceptance>();
    repository.getHandler = () => response.promise;

    const refreshing = store.refresh();
    session.advance();
    response.resolve(ACCEPTED);

    await expect(refreshing).resolves.toBe(false);
    expect(store.acceptance()).toBeNull();
    expect(store.pending()).toBe(false);
    expect(store.errorKey()).toBeNull();
  });

  it('discards a late accept response after logout or session replacement', async () => {
    const { store, repository, session } = setup();
    await store.refresh();
    const response = deferred<LegalAcceptance>();
    repository.acceptHandler = () => response.promise;

    const accepting = store.accept('1.0.0', '1.0.0');
    session.advance();
    response.resolve(ACCEPTED);

    await expect(accepting).resolves.toBe(false);
    expect(store.acceptance()).toBeNull();
    expect(store.pending()).toBe(false);
  });

});
