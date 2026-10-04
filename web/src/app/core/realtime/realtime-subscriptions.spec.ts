import { COALESCE_WINDOW_MS, RealtimeSubscriptions } from './realtime-subscriptions';

describe('RealtimeSubscriptions', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('ref-counts: liveKeys lists a key until its last subscriber leaves', () => {
    const subs = new RealtimeSubscriptions();
    const a = subs.add('ep-1', vi.fn());
    const b = subs.add('ep-1', vi.fn());
    subs.add('ep-2', vi.fn());
    expect(subs.liveKeys()).toEqual(['ep-1', 'ep-2']);
    a.unsubscribe();
    expect(subs.liveKeys()).toEqual(['ep-1', 'ep-2']);
    b.unsubscribe();
    b.unsubscribe();
    expect(subs.liveKeys()).toEqual(['ep-2']);
  });

  it('two subscribers of one key are each called once per window, in registration order', () => {
    const subs = new RealtimeSubscriptions();
    const order: string[] = [];
    subs.add('ep-1', () => order.push('first'));
    subs.add('ep-1', () => order.push('second'));
    subs.notify('ep-1');
    subs.notify('ep-1');
    subs.notify('ep-1');
    expect(order).toEqual([]);
    vi.advanceTimersByTime(COALESCE_WINDOW_MS);
    expect(order).toEqual(['first', 'second']);
  });

  it('window boundary: 249 ms → one call, 251 ms → a second window opens a second call', () => {
    const subs = new RealtimeSubscriptions();
    const handler = vi.fn();
    subs.add('ep-1', handler);
    subs.notify('ep-1');
    vi.advanceTimersByTime(249);
    subs.notify('ep-1');
    vi.advanceTimersByTime(1);
    expect(handler).toHaveBeenCalledTimes(1);
    // The window closed at 250 ms: a notify at 251 ms opens a new one.
    vi.advanceTimersByTime(1);
    subs.notify('ep-1');
    vi.advanceTimersByTime(COALESCE_WINDOW_MS);
    expect(handler).toHaveBeenCalledTimes(2);
  });

  it('a removal inside an open window prevents that handler, not its sibling', () => {
    const subs = new RealtimeSubscriptions();
    const removed = vi.fn();
    const kept = vi.fn();
    const subscription = subs.add('ep-1', removed);
    subs.add('ep-1', kept);
    subs.notify('ep-1');
    vi.advanceTimersByTime(100);
    subscription.unsubscribe();
    vi.advanceTimersByTime(COALESCE_WINDOW_MS);
    expect(removed).not.toHaveBeenCalled();
    expect(kept).toHaveBeenCalledTimes(1);
  });

  it('removing the last subscriber cancels the window timer', () => {
    const subs = new RealtimeSubscriptions();
    const subscription = subs.add('ep-1', vi.fn());
    subs.notify('ep-1');
    expect(vi.getTimerCount()).toBe(1);
    subscription.unsubscribe();
    expect(vi.getTimerCount()).toBe(0);
  });

  it('a throwing handler does not stop the others', () => {
    const subs = new RealtimeSubscriptions();
    const after = vi.fn();
    subs.add('ep-1', () => {
      throw new Error('refetch failed');
    });
    subs.add('ep-1', after);
    subs.notify('ep-1');
    vi.advanceTimersByTime(COALESCE_WINDOW_MS);
    expect(after).toHaveBeenCalledTimes(1);
  });

  it('a key nobody subscribed to opens no window', () => {
    const subs = new RealtimeSubscriptions();
    subs.notify('unknown');
    expect(vi.getTimerCount()).toBe(0);
  });

  it('notifyAll opens one window per live key; clear() cancels every window and registration', () => {
    const subs = new RealtimeSubscriptions();
    const one = vi.fn();
    const two = vi.fn();
    subs.add('ep-1', one);
    subs.add('ep-2', two);
    subs.notifyAll();
    expect(vi.getTimerCount()).toBe(2);
    subs.clear();
    expect(vi.getTimerCount()).toBe(0);
    vi.advanceTimersByTime(COALESCE_WINDOW_MS);
    expect(one).not.toHaveBeenCalled();
    expect(two).not.toHaveBeenCalled();
    expect(subs.liveKeys()).toEqual([]);
  });

  it('cancelPending drops open windows, keeps registrations and lets later notifies open new windows', () => {
    const subs = new RealtimeSubscriptions();
    const handler = vi.fn();
    subs.add('ep-1', handler);
    subs.notify('ep-1');
    subs.cancelPending();
    expect(vi.getTimerCount()).toBe(0);
    vi.advanceTimersByTime(COALESCE_WINDOW_MS * 4);
    expect(handler).not.toHaveBeenCalled();
    expect(subs.liveKeys()).toEqual(['ep-1']);

    subs.notify('ep-1');
    vi.advanceTimersByTime(COALESCE_WINDOW_MS);
    expect(handler).toHaveBeenCalledTimes(1);
  });

  it('cancelPending on an idle registry is a no-op', () => {
    const subs = new RealtimeSubscriptions();
    subs.cancelPending();
    expect(subs.liveKeys()).toEqual([]);
  });
});
