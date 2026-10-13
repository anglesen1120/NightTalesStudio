import { vi } from 'vitest';
import { UnsavedWorkRegistry } from './unsaved-work';

describe('UnsavedWorkRegistry', () => {
  it('nothingRegistered_ConfirmLeaveResolvesTrue', async () => {
    const registry = new UnsavedWorkRegistry();
    expect(registry.hasUnsaved()).toBe(false);
    await expect(registry.confirmLeave()).resolves.toBe(true);
  });

  it('cleanHandler_IsNotAsked', async () => {
    const registry = new UnsavedWorkRegistry();
    const ask = vi.fn(() => Promise.resolve(true));
    registry.register({ hasUnsaved: () => false, confirmLeave: ask });
    expect(registry.hasUnsaved()).toBe(false);
    await expect(registry.confirmLeave()).resolves.toBe(true);
    expect(ask).not.toHaveBeenCalled();
  });

  it('dirtyHandlerLeave_ResolvesTrue', async () => {
    const registry = new UnsavedWorkRegistry();
    const ask = vi.fn(() => Promise.resolve(true));
    registry.register({ hasUnsaved: () => true, confirmLeave: ask });
    expect(registry.hasUnsaved()).toBe(true);
    await expect(registry.confirmLeave()).resolves.toBe(true);
    expect(ask).toHaveBeenCalledTimes(1);
  });

  it('dirtyHandlerStay_ResolvesFalse', async () => {
    const registry = new UnsavedWorkRegistry();
    registry.register({ hasUnsaved: () => true, confirmLeave: () => Promise.resolve(false) });
    await expect(registry.confirmLeave()).resolves.toBe(false);
  });

  it('concurrentCalls_ShareOneFlight', async () => {
    const registry = new UnsavedWorkRegistry();
    let release: ((answer: boolean) => void) | undefined;
    const ask = vi.fn(() => new Promise<boolean>((resolve) => { release = resolve; }));
    registry.register({ hasUnsaved: () => true, confirmLeave: ask });
    const first = registry.confirmLeave();
    const second = registry.confirmLeave();
    expect(ask).toHaveBeenCalledTimes(1);
    release?.(true);
    await expect(first).resolves.toBe(true);
    await expect(second).resolves.toBe(true);
    // The flight is over: the next call asks again.
    void registry.confirmLeave();
    expect(ask).toHaveBeenCalledTimes(2);
    release?.(false);
  });

  it('twoDirtyHandlers_StopAtTheFirstStay', async () => {
    const registry = new UnsavedWorkRegistry();
    const first = vi.fn(() => Promise.resolve(false));
    const second = vi.fn(() => Promise.resolve(true));
    registry.register({ hasUnsaved: () => true, confirmLeave: first });
    registry.register({ hasUnsaved: () => true, confirmLeave: second });
    await expect(registry.confirmLeave()).resolves.toBe(false);
    expect(first).toHaveBeenCalledTimes(1);
    expect(second).not.toHaveBeenCalled();
  });

  it('unregister_RemovesTheHandler', async () => {
    const registry = new UnsavedWorkRegistry();
    const ask = vi.fn(() => Promise.resolve(false));
    const unregister = registry.register({ hasUnsaved: () => true, confirmLeave: ask });
    expect(registry.hasUnsaved()).toBe(true);
    unregister();
    expect(registry.hasUnsaved()).toBe(false);
    await expect(registry.confirmLeave()).resolves.toBe(true);
    expect(ask).not.toHaveBeenCalled();
  });
});
