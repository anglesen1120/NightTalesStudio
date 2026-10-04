import { OutputRef } from '@angular/core';
import { outputToObservable } from '@angular/core/rxjs-interop';
import { Observable } from 'rxjs';

/**
 * Calls `listener` for every emission of an observable or component output.
 *
 * The D2 lint contract bans `.subscribe(` anywhere under shared/ui, specs included. `Observable.forEach` consumes the
 * stream without it; the returned promise settles when the source completes (an output completes with its owner), so
 * it is deliberately not awaited.
 */
export function onEmit<T>(source: Observable<T> | OutputRef<T>, listener: (value: T) => void): void {
  const stream = source instanceof Observable ? source : outputToObservable(source);
  void stream.forEach((value) => listener(value));
}
