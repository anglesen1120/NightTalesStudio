import type * as Signalr from '@microsoft/signalr';
import { HubConnectionPort, type HubConnectionState as PortState } from '../hub-connection.port';

type SignalrModule = typeof Signalr;

// One module promise for the app: the chunk is fetched on the first `start()` only (public pages never pay for it).
let signalrModule: Promise<SignalrModule> | null = null;

function loadSignalr(): Promise<SignalrModule> {
  signalrModule ??= import('@microsoft/signalr').catch((cause: unknown) => {
    signalrModule = null; // a failed chunk fetch must be retryable on the next reconnect attempt
    throw cause;
  });
  return signalrModule;
}

/**
 * The ONLY file that references `@microsoft/signalr` (lint D2/D8), and only through a dynamic `import()` in `start()`
 * so the library lives in its own lazy chunk. WebSockets is the single configured transport, which is what makes
 * `skipNegotiation` legal (FE9). There is deliberately no `accessTokenFactory` (the access token never reaches a URL),
 * `withCredentials` is pinned to `false` (the ticket is the only credential; no cookies ride the upgrade), and there is
 * no `withAutomaticReconnect`: the ticket in the URL is single-use, so every reconnect goes through the service (fresh
 * token, fresh ticket). Logging is off: the library would otherwise print the connect URL, ticket included, to the
 * console.
 */
export class SignalrHubConnection extends HubConnectionPort {
  private connection: Signalr.HubConnection | null = null;
  private lib: SignalrModule | null = null;
  /** Bumped by `stop()`: a `start()` whose module import resolves after a `stop()` must not build a connection. */
  private generation = 0;
  private readonly handlers: { readonly name: string; readonly handler: (payload: unknown) => void }[] = [];
  private readonly closeHandlers: (() => void)[] = [];

  override async start(url: string): Promise<void> {
    const generation = this.generation;
    const lib = await loadSignalr();
    if (generation !== this.generation) throw new Error('Hub connection stopped before it started');
    this.lib = lib;
    const connection = new lib.HubConnectionBuilder()
      .withUrl(url, { transport: lib.HttpTransportType.WebSockets, skipNegotiation: true, withCredentials: false })
      .configureLogging(lib.LogLevel.None)
      .build();
    for (const { name, handler } of this.handlers) {
      connection.on(name, (...args: unknown[]) => {
        handler(args[0]);
      });
    }
    connection.onclose(() => {
      for (const handler of this.closeHandlers) handler();
    });
    this.connection = connection;
    await connection.start();
  }

  override async stop(): Promise<void> {
    this.generation++;
    await this.connection?.stop();
  }

  override state(): PortState {
    if (!this.connection || !this.lib) return 'disconnected';
    switch (this.connection.state) {
      case this.lib.HubConnectionState.Connected:
        return 'connected';
      case this.lib.HubConnectionState.Connecting:
      case this.lib.HubConnectionState.Reconnecting:
        return 'connecting';
      default:
        return 'disconnected';
    }
  }

  override on(name: string, handler: (payload: unknown) => void): void {
    this.handlers.push({ name, handler });
  }

  override onClose(handler: () => void): void {
    this.closeHandlers.push(handler);
  }
}
