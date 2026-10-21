/** Events for one key inside this window collapse into one handler call per subscriber (README D8). */
export const COALESCE_WINDOW_MS = 250;

export interface RealtimeSubscription {
  /** Removes this registration; an open window never calls it afterwards. Safe to call twice. */
  unsubscribe(): void;
}

interface Registration {
  readonly handler: () => void;
}

/**
 * Local, ref-counted registry of episode handlers with a per-key 250 ms coalescing window. Not injectable: the
 * service owns it, `clear()` cancels every open window. The server joins the `user:{id}` group by itself, so a
 * subscription is only this local filter and never a hub call.
 */
export class RealtimeSubscriptions {
  private readonly registrations = new Map<string, Registration[]>();
  /** Open coalescing windows: key → `window.setTimeout` handle. */
  private readonly windows = new Map<string, number>();

  add(key: string, handler: () => void): RealtimeSubscription {
    const registration: Registration = { handler };
    const list = this.registrations.get(key);
    if (list === undefined) this.registrations.set(key, [registration]);
    else list.push(registration);
    return { unsubscribe: () => { this.remove(key, registration); } };
  }

  remove(key: string, registration: Registration): void {
    const list = this.registrations.get(key);
    if (list === undefined) return;
    const index = list.indexOf(registration);
    if (index < 0) return;
    list.splice(index, 1);
    if (list.length > 0) return;
    this.registrations.delete(key);
    const open = this.windows.get(key);
    if (open !== undefined) {
      window.clearTimeout(open);
      this.windows.delete(key);
    }
  }

  /** Keys with at least one live subscriber. */
  liveKeys(): readonly string[] {
    return [...this.registrations.keys()];
  }

  /** Opens the key's window when none is open; later calls inside it add nothing. Unknown keys are dropped. */
  notify(key: string): void {
    if (!this.registrations.has(key) || this.windows.has(key)) return;
    const timer = window.setTimeout(() => {
      this.windows.delete(key);
      this.flush(key);
    }, COALESCE_WINDOW_MS);
    this.windows.set(key, timer);
  }

  /** Calls every live subscriber of every key once, outside any window (the `resync` after a (re)connect). */
  notifyAll(): void {
    for (const key of this.registrations.keys()) this.notify(key);
  }

  /** Cancels every open window but keeps the registrations (the pause path: `resume()` resyncs everyone anyway). */
  cancelPending(): void {
    for (const open of this.windows.values()) window.clearTimeout(open);
    this.windows.clear();
  }

  clear(): void {
    this.cancelPending();
    this.registrations.clear();
  }

  private flush(key: string): void {
    // Snapshot: a handler may unsubscribe itself or a sibling; a removed sibling is not called.
    const snapshot = [...(this.registrations.get(key) ?? [])];
    for (const registration of snapshot) {
      if (!this.registrations.get(key)?.includes(registration)) continue;
      try {
        registration.handler();
      } catch {
        // The store owns its error state; one failing refetch never breaks the window.
      }
    }
  }
}
