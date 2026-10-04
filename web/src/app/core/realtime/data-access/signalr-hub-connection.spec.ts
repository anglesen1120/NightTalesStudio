import { HttpTransportType, HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { SignalrHubConnection } from './signalr-hub-connection';

// Absolute: SignalR resolves a relative URL only when `Platform.isBrowser`, which is false under Vitest (Node `process`).
const HUB_URL = 'http://localhost/hubs/episodes?ticket=abc';

// The real builder runs; only the network edge (`HubConnection.start/stop`) and the observation points are spied.
describe('SignalrHubConnection', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  function spyNetwork(): void {
    vi.spyOn(HubConnection.prototype, 'start').mockResolvedValue(undefined);
    vi.spyOn(HubConnection.prototype, 'stop').mockResolvedValue(undefined);
  }

  it('starts with WebSockets only and skipNegotiation, and nothing else in the options', async () => {
    spyNetwork();
    const withUrl = vi.spyOn(HubConnectionBuilder.prototype, 'withUrl');
    const reconnect = vi.spyOn(HubConnectionBuilder.prototype, 'withAutomaticReconnect');
    const socket = new SignalrHubConnection();
    await socket.start(HUB_URL);

    expect(withUrl).toHaveBeenCalledTimes(1);
    const [url, options] = withUrl.mock.calls[0] as [string, Record<string, unknown>];
    expect(url).toBe(HUB_URL);
    expect(options).toEqual({ transport: HttpTransportType.WebSockets, skipNegotiation: true, withCredentials: false });
    expect(options).not.toHaveProperty('accessTokenFactory');
    expect(options).not.toHaveProperty('headers');
    // The ticket is single-use: SignalR must never reconnect by itself.
    expect(reconnect).not.toHaveBeenCalled();
  });

  it('turns library logging off so the connect URL (the ticket) never reaches the console', async () => {
    spyNetwork();
    const logging = vi.spyOn(HubConnectionBuilder.prototype, 'configureLogging');
    await new SignalrHubConnection().start(HUB_URL);
    expect(logging).toHaveBeenCalledWith(LogLevel.None);
  });

  it('registers handlers made before start, passes only the first argument, and relays close', async () => {
    spyNetwork();
    const registered = new Map<string, (...args: unknown[]) => void>();
    vi.spyOn(HubConnection.prototype, 'on').mockImplementation((name: string, callback: (...args: unknown[]) => void) => {
      registered.set(name, callback);
    });
    let onClose: (() => void) | undefined;
    vi.spyOn(HubConnection.prototype, 'onclose').mockImplementation((callback: (error?: Error) => void) => {
      onClose = () => {
        callback();
      };
    });
    const socket = new SignalrHubConnection();
    const received: unknown[] = [];
    const closed = vi.fn();
    socket.on('EpisodeUpdated', (payload) => received.push(payload));
    socket.onClose(closed);
    expect(registered.size).toBe(0);

    await socket.start(HUB_URL);
    registered.get('EpisodeUpdated')?.({ ids: {} }, 'ignored');
    onClose?.();

    expect(received).toEqual([{ ids: {} }]);
    expect(closed).toHaveBeenCalledTimes(1);
  });

  it('is disconnected before start and stop() before start resolves', async () => {
    const socket = new SignalrHubConnection();
    expect(socket.state()).toBe('disconnected');
    await expect(socket.stop()).resolves.toBeUndefined();
  });

  it('stop() stops the underlying connection', async () => {
    vi.spyOn(HubConnection.prototype, 'start').mockResolvedValue(undefined);
    const stop = vi.spyOn(HubConnection.prototype, 'stop').mockResolvedValue(undefined);
    const socket = new SignalrHubConnection();
    await socket.start(HUB_URL);
    await socket.stop();
    expect(stop).toHaveBeenCalledTimes(1);
  });

  it('state() follows the underlying connection', async () => {
    spyNetwork();
    const socket = new SignalrHubConnection();
    await socket.start(HUB_URL);
    expect(socket.state()).toBe('disconnected'); // the real connection never connected (network edge is mocked)
  });

  it('stop() while the library is still loading builds no connection and rejects the pending start()', async () => {
    const start = vi.spyOn(HubConnection.prototype, 'start').mockResolvedValue(undefined);
    const build = vi.spyOn(HubConnectionBuilder.prototype, 'build');
    const socket = new SignalrHubConnection();
    const pending = socket.start(HUB_URL);
    await socket.stop();
    await expect(pending).rejects.toThrow('stopped before it started');
    expect(build).not.toHaveBeenCalled();
    expect(start).not.toHaveBeenCalled();
    expect(socket.state()).toBe('disconnected');
  });
});
