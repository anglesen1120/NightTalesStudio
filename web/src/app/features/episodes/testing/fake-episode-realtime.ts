import type { RealtimeSubscription } from '@core/realtime';

export class FakeEpisodeRealtime {
  readonly subscriptions: Array<{ id: string; handler: () => void; unsubscribed: boolean }> = [];
  readonly callLog: string[];

  constructor(callLog: string[] = []) {
    this.callLog = callLog;
  }

  subscribeEpisode(id: string, handler: () => void): RealtimeSubscription {
    const entry = { id, handler, unsubscribed: false };
    this.subscriptions.push(entry);
    this.callLog.push('subscribe');
    return { unsubscribe: () => {
      if (entry.unsubscribed) return;
      entry.unsubscribed = true;
      this.callLog.push('unsubscribe');
    } };
  }

  emit(id: string): void {
    for (const entry of this.subscriptions) {
      if (entry.id === id && !entry.unsubscribed) entry.handler();
    }
  }

  resync(): void {
    for (const entry of this.subscriptions) {
      if (!entry.unsubscribed) entry.handler();
    }
  }
}
