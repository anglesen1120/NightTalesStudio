import { HubConnectionPort, type HubConnectionState } from '../hub-connection.port';

/** The options `SignalrHubConnection` passes to SignalR, as the contract a spec asserts on the fake. */
export interface RecordedHubOptions {
  readonly transport: 'WebSockets';
  readonly skipNegotiation: true;
}

/**
 * Hand-written scriptable `HubConnectionPort` (README D15). Records every `start(url)`, lets a test resolve/reject the
 * pending start, fire events and fire `onClose`. `fire` only reaches names the service registered, like the real socket.
 */
export class FakeHubConnection extends HubConnectionPort {
  readonly startedUrls: string[] = [];
  /** Same shape the adapter hands SignalR: WebSockets only, negotiation skipped, nothing else. */
  readonly recordedOptions: RecordedHubOptions = { transport: 'WebSockets', skipNegotiation: true };
  stopCalls = 0;
  /** `'ok'` resolves `start()` at once, `'deferred'` waits for `resolveStart`/`rejectStart`, `'reject'` rejects. */
  startMode: 'ok' | 'deferred' | 'reject' = 'ok';
  startError: unknown = new Error('socket failed');

  private current: HubConnectionState = 'disconnected';
  private readonly handlers = new Map<string, ((payload: unknown) => void)[]>();
  private readonly closeHandlers: (() => void)[] = [];
  private pending: { resolve: () => void; reject: (error: unknown) => void } | null = null;

  override start(url: string): Promise<void> {
    this.startedUrls.push(url);
    this.current = 'connecting';
    if (this.startMode === 'reject') {
      this.current = 'disconnected';
      // A browser's failed WebSocket upgrade rejects with a plain `Error` that carries no HTTP status.
      return Promise.reject(this.startError instanceof Error ? this.startError : new Error('hub start failed'));
    }
    if (this.startMode === 'deferred') {
      return new Promise<void>((resolve, reject) => {
        this.pending = { resolve, reject };
      });
    }
    this.current = 'connected';
    return Promise.resolve();
  }

  override stop(): Promise<void> {
    this.stopCalls += 1;
    this.current = 'disconnected';
    return Promise.resolve();
  }

  override state(): HubConnectionState {
    return this.current;
  }

  override on(name: string, handler: (payload: unknown) => void): void {
    const list = this.handlers.get(name);
    if (list === undefined) this.handlers.set(name, [handler]);
    else list.push(handler);
  }

  override onClose(handler: () => void): void {
    this.closeHandlers.push(handler);
  }

  /** Names the service registered with `on`. */
  get registeredNames(): readonly string[] {
    return [...this.handlers.keys()];
  }

  resolveStart(): void {
    this.current = 'connected';
    this.pending?.resolve();
    this.pending = null;
  }

  rejectStart(error: unknown): void {
    this.current = 'disconnected';
    this.pending?.reject(error);
    this.pending = null;
  }

  /** Delivers a server event; a name nobody registered reaches nobody. */
  fire(name: string, payload: unknown): void {
    for (const handler of this.handlers.get(name) ?? []) handler(payload);
  }

  /**
   * Delivers a server event the way SignalR does: the incoming name is matched case-insensitively against what the
   * service registered, `Object.prototype` names reach nobody. Returns how many handlers ran.
   */
  fireRaw(name: string, payload: unknown): number {
    const wanted = name.toLowerCase();
    let ran = 0;
    for (const [registered, list] of this.handlers) {
      if (registered.toLowerCase() !== wanted) continue;
      for (const handler of list) {
        handler(payload);
        ran += 1;
      }
    }
    return ran;
  }

  /** The server (or network) ended the connection. */
  fireClose(): void {
    this.current = 'disconnected';
    for (const handler of this.closeHandlers) handler();
  }
}
