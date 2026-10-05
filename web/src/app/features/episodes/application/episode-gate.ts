import { InjectionToken } from '@angular/core';
import type { Signal } from '@angular/core';
import type { GateState } from '../domain/episode-gate';

export const EPISODE_GATE = new InjectionToken<Signal<GateState>>('EPISODE_GATE');
