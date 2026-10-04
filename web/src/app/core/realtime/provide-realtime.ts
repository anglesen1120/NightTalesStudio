import { isPlatformBrowser } from '@angular/common';
import {
  EnvironmentProviders,
  Injector,
  PLATFORM_ID,
  inject,
  makeEnvironmentProviders,
  provideEnvironmentInitializer,
} from '@angular/core';
import { SESSION_SCOPED } from '@core/session';
import { HubTicketHttpRepository } from './data-access/hub-ticket.http-repository';
import { SignalrHubConnection } from './data-access/signalr-hub-connection';
import { HUB_CONNECTION_FACTORY } from './hub-connection.port';
import { HubTicketRepository } from './hub-ticket.repository';
import { RealtimeService } from './realtime.service';

/**
 * Wires the realtime seam (README D5/D8): the HTTP ticket repository, the SignalR adapter factory, the `SESSION_SCOPED`
 * entry and the root service itself (created at startup in the browser so its `SessionStore.status` effect runs).
 * The scope entry resolves the service lazily: `SessionStore` injects `SESSION_SCOPED` and the service injects
 * `SessionStore`, so a direct reference would be a cycle.
 */
export function provideRealtime(): EnvironmentProviders {
  return makeEnvironmentProviders([
    { provide: HubTicketRepository, useClass: HubTicketHttpRepository },
    { provide: HUB_CONNECTION_FACTORY, useValue: () => new SignalrHubConnection() },
    {
      provide: SESSION_SCOPED,
      multi: true,
      useFactory: () => {
        const injector = inject(Injector);
        return {
          reset: (): void => {
            injector.get(RealtimeService).reset();
          },
        };
      },
    },
    provideEnvironmentInitializer(() => {
      if (isPlatformBrowser(inject(PLATFORM_ID))) inject(RealtimeService);
    }),
  ]);
}
