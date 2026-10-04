import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, InjectionToken, inject, isDevMode } from '@angular/core';
import type { AppError } from './app-error';
import type { ProblemWire } from './data-access/problem-wire';
import { codeForStatus, fieldMessageKeyOf, isKnownCode, kindFor, messageKeyOf } from './error-code';

/** Whether the dev-only `client_bug` console line is written. Provided from `isDevMode()`; a spec overrides it. */
export const APP_ERROR_DEV_MODE = new InjectionToken<boolean>('APP_ERROR_DEV_MODE', {
  providedIn: 'root',
  factory: () => isDevMode(),
});

/** A body string longer than this is not parsed (a proxy page, not a ProblemDetails). */
const MAX_BODY_TEXT_LENGTH = 65_536;

type FieldErrors = Readonly<Record<string, readonly string[]>>;

/** A key that could reach `Object.prototype` if it were assigned onto an object. Never a real field name. */
function isBlockedKey(key: string): boolean {
  return key === '__proto__' || key === 'constructor' || key === 'prototype';
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  if (typeof value !== 'object' || value === null) return false;
  const proto: unknown = Object.getPrototypeOf(value);
  return proto === Object.prototype || proto === null;
}

function isScalarOrNull(value: unknown): value is string | number | boolean | null {
  return value === null || typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean';
}

/** Only arrays of strings survive, in payload order; a key left with no string is dropped. */
function sanitizeFieldErrors(errors: Record<string, unknown>): FieldErrors {
  const fields = Object.create(null) as Record<string, readonly string[]>;
  for (const [pointer, messages] of Object.entries(errors)) {
    if (isBlockedKey(pointer) || !Array.isArray(messages)) continue;
    const strings = messages.filter((message): message is string => typeof message === 'string');
    if (strings.length > 0) fields[pointer] = Object.freeze(strings);
  }
  return Object.freeze(fields);
}

/** `keywords_flagged` rows (`segment`, `index`, `rule`): plain objects reduced to their scalar members. */
function sanitizeRows(rows: readonly unknown[]): readonly Readonly<Record<string, unknown>>[] {
  const out: Readonly<Record<string, unknown>>[] = [];
  for (const row of rows) {
    if (!isPlainObject(row)) continue;
    const copy = Object.create(null) as Record<string, unknown>;
    for (const [key, value] of Object.entries(row)) {
      if (!isBlockedKey(key) && isScalarOrNull(value)) copy[key] = value;
    }
    out.push(Object.freeze(copy));
  }
  return Object.freeze(out);
}

interface ReadBody {
  readonly problem: ProblemWire | undefined;
  readonly fieldErrors: FieldErrors | undefined;
}

/**
 * Copies the allow-listed members of a parsed body (anything else — nested objects, unknown members — is dropped) and
 * extracts the field errors. A non-object body (string, Blob, ProgressEvent, null) yields nothing.
 */
function readBody(raw: unknown): ReadBody {
  if (!isPlainObject(raw)) return { problem: undefined, fieldErrors: undefined };
  const { code, traceId, type, title, detail, instance, status, errors, retryAfterSeconds, field, limit, mfaToken } = raw;
  const problem: { -readonly [K in keyof ProblemWire]: ProblemWire[K] } = {};
  let fieldErrors: FieldErrors | undefined;
  if (typeof code === 'string') problem.code = code;
  if (typeof traceId === 'string') problem.traceId = traceId;
  if (typeof type === 'string' || type === null) problem.type = type;
  if (typeof title === 'string' || title === null) problem.title = title;
  if (typeof detail === 'string' || detail === null) problem.detail = detail;
  if (typeof instance === 'string' || instance === null) problem.instance = instance;
  if (typeof status === 'number' || typeof status === 'string' || status === null) problem.status = status;
  if (typeof retryAfterSeconds === 'number') problem.retryAfterSeconds = retryAfterSeconds;
  if (typeof field === 'string') problem.field = field;
  if (typeof limit === 'number') problem.limit = limit;
  if (typeof mfaToken === 'string') problem.mfaToken = mfaToken;
  if (Array.isArray(errors)) {
    problem.errors = sanitizeRows(errors as readonly unknown[]);
  } else if (isPlainObject(errors)) {
    fieldErrors = sanitizeFieldErrors(errors);
    problem.errors = fieldErrors;
  }
  return { problem: Object.freeze(problem), fieldErrors };
}

/** `HttpErrorResponse.error` is a parsed object, a string (non-JSON body), a Blob, a ProgressEvent or null. */
function parseBody(body: unknown): unknown {
  if (typeof body !== 'string') return body;
  const text = body.trimStart();
  if (!text.startsWith('{') || text.length > MAX_BODY_TEXT_LENGTH) return undefined;
  try {
    return JSON.parse(text) as unknown;
  } catch {
    return undefined;
  }
}

/** `Retry-After` is integer seconds; an HTTP-date, a negative or a fractional value is treated as absent. */
function parseRetryAfter(header: string | null): number | null {
  const text = header?.trim() ?? '';
  return /^\d{1,9}$/.test(text) ? Number(text) : null;
}

/**
 * The one place an HTTP failure becomes an `AppError` (README D9). Copy is only ever looked up by key: `detail`,
 * `title` and `type` of the body are kept on `problem` but never feed a message key or a message.
 */
@Injectable({ providedIn: 'root' })
export class AppErrorMapper {
  private readonly devMode = inject(APP_ERROR_DEV_MODE);

  /** Accepts an `HttpErrorResponse`, an `Error` or anything else; never throws. */
  from(error: unknown): AppError {
    if (error instanceof HttpErrorResponse) {
      return this.fromBody(parseBody(error.error), error.status, parseRetryAfter(error.headers.get('Retry-After')));
    }
    // Not an HTTP failure (a thrown Error, a rejected string): there is no status or code to read.
    return this.build('unknown_error', 0, readBody(undefined), null);
  }

  /**
   * `retryAfter` is the parsed seconds of the `Retry-After` header (`null` when absent or unusable). The body `status`
   * is not trusted: the HTTP `status` decides the fallback. A known body code wins over the status, an unknown
   * non-empty one maps to `unknown_error`, and a missing one falls back to the status table.
   */
  fromProblem(problem: ProblemWire | null, status: number, retryAfter: number | null): AppError {
    return this.fromBody(problem, status, retryAfter);
  }

  /** `errors.<code>`; `errors.unknown_error` for a code without a leaf; `unauthenticated` → `errors.session_revoked`. */
  messageKey(error: AppError): string {
    return messageKeyOf(error);
  }

  /** The key a control bound to `ptr` shows (see `fieldMessageKeyOf`). */
  fieldMessageKey(error: AppError, ptr: string): string {
    return fieldMessageKeyOf(error, ptr);
  }

  /** The alert footer line for the trace id (`errors.unknown.trace`), or `null` when the response carried none. */
  traceFooter(error: AppError): { key: 'errors.unknown.trace'; params: { traceId: string } } | null {
    return error.traceId === undefined ? null : { key: 'errors.unknown.trace', params: { traceId: error.traceId } };
  }

  private fromBody(rawBody: unknown, status: number, retryAfter: number | null): AppError {
    const body = readBody(rawBody);
    const wireCode = body.problem?.code ?? '';
    let code: string;
    if (status === 0) code = 'network_error';
    else if (wireCode === '') code = codeForStatus(status);
    else code = isKnownCode(wireCode) ? wireCode : 'unknown_error';
    const seconds = retryAfter !== null && Number.isSafeInteger(retryAfter) && retryAfter >= 0 ? retryAfter : null;
    return this.build(code, status, body, seconds);
  }

  private build(code: string, status: number, body: ReadBody, retryAfter: number | null): AppError {
    const kind = kindFor(code, status);
    const traceId = body.problem?.traceId;
    const hasTraceId = traceId !== undefined && traceId !== '';
    if (kind === 'client_bug' && this.devMode) {
      // One line, no body: code/status/traceId only (task Design).
      console.warn(`client_bug ${code}/${status}/${hasTraceId ? traceId : '-'}`);
    }
    return Object.freeze({
      code,
      status,
      kind,
      fieldErrors: body.fieldErrors ?? Object.freeze(Object.create(null) as Record<string, readonly string[]>),
      ...(retryAfter === null ? {} : { retryAfterSec: retryAfter }),
      ...(hasTraceId ? { traceId } : {}),
      ...(body.problem === undefined ? {} : { problem: body.problem }),
    });
  }
}
