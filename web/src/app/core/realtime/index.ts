// Public surface of core/realtime. `data-access/` (the SignalR adapter and the generated-client repository) and
// `testing/` are deliberately not re-exported; `provideRealtime()` wires the adapter and the repository.
export * from './backoff';
export * from './hub-connection.port';
export * from './hub-ticket.repository';
export * from './provide-realtime';
export * from './realtime-subscriptions';
export * from './realtime.model';
export * from './realtime.service';
