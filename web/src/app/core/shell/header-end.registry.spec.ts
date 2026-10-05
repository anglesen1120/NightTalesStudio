import { DestroyRef, EnvironmentInjector, createEnvironmentInjector, type TemplateRef } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { HeaderEndRegistry } from './header-end.registry';

const template = (): TemplateRef<unknown> => ({}) as TemplateRef<unknown>;

function setup(): { registry: HeaderEndRegistry; owner: () => { ref: DestroyRef; destroy: () => void } } {
  TestBed.configureTestingModule({ providers: [...ZONELESS_TEST_PROVIDERS, HeaderEndRegistry] });
  const parent = TestBed.inject(EnvironmentInjector);
  return {
    registry: TestBed.inject(HeaderEndRegistry),
    owner: () => {
      const injector = createEnvironmentInjector([], parent);
      return { ref: injector.get(DestroyRef), destroy: () => injector.destroy() };
    },
  };
}

describe('HeaderEndRegistry', () => {
  it('starts empty and exposes the registered template as the render source', () => {
    const { registry, owner } = setup();
    expect(registry.template()).toBeNull();
    const a = template();
    registry.register(a, owner().ref);
    expect(registry.template()).toBe(a);
  });

  it('clears the slot when the owner is destroyed', () => {
    const { registry, owner } = setup();
    const page = owner();
    registry.register(template(), page.ref);
    page.destroy();
    expect(registry.template()).toBeNull();
  });

  it("A's late disposer cannot erase B's slot", () => {
    const { registry, owner } = setup();
    const a = owner();
    const b = owner();
    const disposeA = registry.register(template(), a.ref);
    const tb = template();
    registry.register(tb, b.ref);
    disposeA();
    expect(registry.template()).toBe(tb);
    a.destroy();
    expect(registry.template()).toBe(tb);
  });

  it("A's destroy after B registered leaves B's slot, and B's destroy clears it", () => {
    const { registry, owner } = setup();
    const a = owner();
    const b = owner();
    registry.register(template(), a.ref);
    const tb = template();
    registry.register(tb, b.ref);
    a.destroy();
    expect(registry.template()).toBe(tb);
    b.destroy();
    expect(registry.template()).toBeNull();
  });

  it('the disposer clears its own registration and is idempotent', () => {
    const { registry, owner } = setup();
    const dispose = registry.register(template(), owner().ref);
    dispose();
    dispose();
    expect(registry.template()).toBeNull();
  });
});
