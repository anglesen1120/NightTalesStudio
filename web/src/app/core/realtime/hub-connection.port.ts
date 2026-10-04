import { InjectionToken } from '@angular/core';

export type HubConnectionState = 'disconnected' | 'connecting' | 'connected';

/**
 * The only seam that knows about the socket library (README D8). One instance is one connection attempt: the service
 * asks the factory for a fresh one per connect, registers `on`/`onClose` first, then `start(url)`.
 */
export abstract class HubConnectionPort {
  /** Opens the socket. Rejects when it cannot open; the URL carries the ticket and must never be logged. */
  abstract start(url: string): Promise<void>;
  abstract stop(): Promise<void>;
  abstract state(): HubConnectionState;
  /** Registers a client method; the handler receives the first argument the server sent. */
  abstract on(name: string, handler: (payload: unknown) => void): void;
  /** Fires on every end of an opened connection (server abort, network drop, `stop()`). */
  abstract onClose(handler: () => void): void;
}

export const HUB_CONNECTION_FACTORY = new InjectionToken<() => HubConnectionPort>('HUB_CONNECTION_FACTORY');
