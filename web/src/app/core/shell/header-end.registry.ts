import { DestroyRef, Injectable, type Signal, type TemplateRef, computed, signal } from '@angular/core';

interface Registration {
  readonly template: TemplateRef<unknown>;
}

/**
 * The shell's `header-end` slot (README D14), provided by `ShellComponent` (component scope). A routed page registers
 * one template; the shell forwards it to the top bar's `NgTemplateOutlet`. Every registration is a fresh token: the
 * disposer (or the owner's destroy) clears the slot only while its own token is still the current one, so a late
 * disposer of page A can never erase page B's template.
 */
@Injectable()
export class HeaderEndRegistry {
  private readonly current = signal<Registration | null>(null);
  readonly template: Signal<TemplateRef<unknown> | null> = computed(() => this.current()?.template ?? null);

  register(template: TemplateRef<unknown>, owner: DestroyRef): () => void {
    const token: Registration = { template };
    this.current.set(token);
    let disposed = false;
    const dispose = (): void => {
      if (disposed) return;
      disposed = true;
      if (this.current() === token) this.current.set(null);
    };
    const unregisterDestroy = owner.onDestroy(dispose);
    return () => {
      unregisterDestroy();
      dispose();
    };
  }
}
