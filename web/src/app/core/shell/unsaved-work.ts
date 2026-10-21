import { Injectable } from '@angular/core';

/** One page-scoped unsaved-work source; the page registers itself and unregisters with its `DestroyRef`. */
export interface UnsavedWorkHandler {
  hasUnsaved(): boolean;
  /** Drop the draft without asking (a terminal `SessionEnded`); a no-op when there is nothing left to lose. */
  discard(): void;
  /** Resolves `true` when the user accepts losing the work (or when there is nothing left to lose). */
  confirmLeave(): Promise<boolean>;
}

/**
 * The shell-wide "you have unsaved work" port (10.5.9-T1). `ShellStore.logOut()` consults it before ending a
 * session, so a logout with a dirty draft asks exactly like a route change does. A terminal `SessionEnded`
 * (`session_revoked`, `refresh_token_reused`, an expired restore) is not the user's decision: `SessionStore.endSession`
 * discards every draft through `discardAll()` before its navigation, so the route's dirty guard runs against a clean
 * draft and never opens the prompt.
 *
 * Concurrent calls share one in-flight prompt, and each handler is asked once per flight.
 */
@Injectable({ providedIn: 'root' })
export class UnsavedWorkRegistry {
  private readonly handlers = new Set<UnsavedWorkHandler>();
  private flight: Promise<boolean> | null = null;

  register(handler: UnsavedWorkHandler): () => void {
    this.handlers.add(handler);
    return () => { this.handlers.delete(handler); };
  }

  /** The synchronous read `ShellStore.logOut()` uses to skip the async path entirely for a clean session. */
  hasUnsaved(): boolean {
    for (const handler of this.handlers) if (handler.hasUnsaved()) return true;
    return false;
  }

  /**
   * Drop every source's draft without asking: `SessionStore.endSession` calls this for a terminal `SessionEnded`
   * before navigating to `/login`, so the dirty guard runs against a clean draft. A handler with nothing to lose
   * no-ops.
   */
  discardAll(): void {
    for (const handler of this.handlers) {
      // A handler's `discard()` is its own teardown and may throw (a draft whose teardown failed). The caller is
      // `SessionStore.endSession`, which navigates to `/login` right after; a throw here must not abort that redirect,
      // so the remaining handlers are still dropped.
      try { handler.discard(); } catch { /* keep discarding the rest */ }
    }
  }

  confirmLeave(): Promise<boolean> {
    const dirty: UnsavedWorkHandler[] = [];
    for (const handler of this.handlers) if (handler.hasUnsaved()) dirty.push(handler);
    if (dirty.length === 0) return Promise.resolve(true);
    if (this.flight !== null) return this.flight;
    const flight = this.askEach(dirty).finally(() => { if (this.flight === flight) this.flight = null; });
    this.flight = flight;
    return flight;
  }

  private async askEach(handlers: readonly UnsavedWorkHandler[]): Promise<boolean> {
    for (const handler of handlers) if (!(await handler.confirmLeave())) return false;
    return true;
  }
}
