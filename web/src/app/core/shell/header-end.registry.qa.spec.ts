import { DestroyRef, EnvironmentInjector, createEnvironmentInjector, type TemplateRef } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { HeaderEndRegistry } from './header-end.registry';

// QA (10.2.6-T1): disposer / owner-destroy races of the header-end slot.

const template = (): TemplateRef<unknown> => ({}) as TemplateRef<unknown>;

function setup() {
  TestBed.configureTestingModule({ providers: [...ZONELESS_TEST_PROVIDERS, HeaderEndRegistry] });
  const parent = TestBed.inject(EnvironmentInjector);
  const owner = () => {
    const injector = createEnvironmentInjector([], parent);
    return { ref: injector.get(DestroyRef), destroy: () => injector.destroy() };
  };
  return { registry: TestBed.inject(HeaderEndRegistry), owner };
}

describe('HeaderEndRegistry (QA races)', () => {
  it("A's owner destroyed AFTER B registered does not erase B (destroy path, not just disposer)", () => {
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

  it('calling the disposer twice, then destroying the owner, never clears a newer registration', () => {
    const { registry, owner } = setup();
    const a = owner();
    const b = owner();
    const disposeA = registry.register(template(), a.ref);
    const tb = template();
    registry.register(tb, b.ref);
    disposeA();
    disposeA();
    a.destroy();
    expect(registry.template()).toBe(tb);
  });

  it('the same owner registering twice: the older disposer cannot clear the newer template', () => {
    const { registry, owner } = setup();
    const page = owner();
    const first = registry.register(template(), page.ref);
    const t2 = template();
    registry.register(t2, page.ref);
    first();
    expect(registry.template()).toBe(t2);
  });

  it('registering the same TemplateRef again after dispose re-shows it', () => {
    const { registry, owner } = setup();
    const page = owner();
    const t = template();
    registry.register(t, page.ref)();
    expect(registry.template()).toBeNull();
    registry.register(t, page.ref);
    expect(registry.template()).toBe(t);
  });

  it("disposing B then A's late disposer leaves the slot empty and does not throw", () => {
    const { registry, owner } = setup();
    const disposeA = registry.register(template(), owner().ref);
    const disposeB = registry.register(template(), owner().ref);
    disposeB();
    expect(registry.template()).toBeNull();
    expect(() => disposeA()).not.toThrow();
    expect(registry.template()).toBeNull();
  });
});
