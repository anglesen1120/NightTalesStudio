import { Injectable } from '@angular/core';

/** One page-scoped unsaved-work source; the page registers itself and unregisters with its `DestroyRef`. */
export interface UnsavedWorkHandler {
  hasUnsaved(): boolean;
  /** Resolves `true` when the user accepts losing the work (or when there is nothing left to lose). */
  confirmLeave(): Promise<boolean>;
}

/**
 * The shell-wide "you have unsaved work" port (10.5.9-T1). `ShellStore.logOut()` consults it before ending a
 * session, so a logout with a dirty draft asks exactly like a route change does. A terminal `SessionEnded`
 * (`session_revoked`, `refresh_token_reused`, an expired restore) bypasses it: it ends the session directly through
 * `SessionStore.signOut()`, so no prompt can keep an invalid session or its draft alive.
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
