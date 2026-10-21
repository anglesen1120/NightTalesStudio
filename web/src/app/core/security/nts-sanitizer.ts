import { Injectable, InjectionToken, inject } from '@angular/core';

/**
 * The ONE reviewed HTML-producing module (README D4/D12, WR12). The CSP header allow-lists exactly three Trusted
 * Types policies - `angular`, `angular#bundler` and `nts-sanitizer` - so this is the only place a TrustedHTML can
 * be minted by application code. It is unused in phase P (legal blocks render without `innerHTML`, D12); it
 * exists so a future need has exactly one reviewed source -> sink path. Adding a second policy, or a method that
 * takes an arbitrary HTML string, is a security-review change, not a convenience.
 *
 * The only input is the closed block model produced by `tools/web/legal-to-json.mjs`; every field is escaped and
 * every block is rendered through a fixed template. A block whose `kind` is outside the closed set is dropped.
 */
export type LegalBlock = {
  kind: 'heading' | 'paragraph' | 'list' | 'quote';
  text?: string;
  items?: readonly string[];
  level?: number;
};

/**
 * The platform's `TrustedHTML` (lib.dom does not declare the type and no `@types/trusted-types` is installed):
 * opaque, produced only by a policy.
 */
export interface TrustedHtml {
  toString(): string;
}

/** The handle `createPolicy` returns: the only way to turn a string into TrustedHTML. */
export interface TrustedHtmlPolicy {
  createHTML(input: string): TrustedHtml;
}

/** The subset of `window.trustedTypes` this module uses (jsdom does not implement it; specs inject a fake). */
export interface TrustedTypesFactory {
  createPolicy(name: string, rules: { createHTML(input: string): string }): TrustedHtmlPolicy;
}

/** `window.trustedTypes` when the browser implements it, else null. Checked at runtime, never assumed. */
function browserTrustedTypes(): TrustedTypesFactory | null {
  const candidate: unknown = Reflect.get(globalThis, 'trustedTypes');
  if (typeof candidate === 'object' && candidate !== null && 'createPolicy' in candidate) {
    // Shape verified just above; `createPolicy` is the only member used.
    const factory = candidate as TrustedTypesFactory;
    return factory;
  }
  return null;
}

export const TRUSTED_TYPES = new InjectionToken<TrustedTypesFactory | null>('TRUSTED_TYPES', {
  providedIn: 'root',
  factory: browserTrustedTypes,
});

// createPolicy('nts-sanitizer') may run once per page (the header has no `allow-duplicates`), so the handle is
// cached per factory. `pending` is the only string the policy callback will accept: the one the closed builder
// below has just produced, so the handle cannot be used to launder any other string even if it leaked.
const policies = new WeakMap<TrustedTypesFactory, TrustedHtmlPolicy>();
let pending: string | null = null;

const ENTITIES: Record<string, string> = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };
const escapeHtml = (value: unknown): string =>
  (typeof value === 'string' ? value : '').replace(/[&<>"']/g, (character) => ENTITIES[character] ?? character);

const headingLevel = (level: unknown): number =>
  typeof level === 'number' && Number.isInteger(level) ? Math.min(6, Math.max(1, level)) : 2;

function renderBlock(block: LegalBlock): string {
  switch (block.kind) {
    case 'heading': {
      const tag = `h${headingLevel(block.level)}`;
      return `<${tag}>${escapeHtml(block.text)}</${tag}>`;
    }
    case 'paragraph':
      return `<p>${escapeHtml(block.text)}</p>`;
    case 'quote':
      return `<blockquote><p>${escapeHtml(block.text)}</p></blockquote>`;
    case 'list': {
      const items = Array.isArray(block.items) ? (block.items as readonly unknown[]) : [];
      return `<ul>${items.map((item) => `<li>${escapeHtml(item)}</li>`).join('')}</ul>`;
    }
    default:
      return ''; // outside the closed set: dropped, never passed through
  }
}

@Injectable({ providedIn: 'root' })
export class NtsSanitizer {
  static readonly POLICY_NAME = 'nts-sanitizer';

  private readonly trustedTypes = inject(TRUSTED_TYPES);

  /**
   * Renders the closed block model to trusted HTML. Returns the same escaped markup as a plain string when the
   * browser has no Trusted Types (Angular's own sanitizer then still applies to a string binding).
   */
  toTrustedHtml(blocks: readonly LegalBlock[]): TrustedHtml | string {
    const html = (Array.isArray(blocks) ? blocks : []).map(renderBlock).join('');
    const factory = this.trustedTypes;
    if (factory === null) {
      return html;
    }
    let policy = policies.get(factory);
    if (policy === undefined) {
      policy = factory.createPolicy(NtsSanitizer.POLICY_NAME, {
        createHTML: (input) => {
          if (input !== pending) {
            throw new TypeError('nts-sanitizer only accepts markup built from the closed block model');
          }
          return input;
        },
      });
      policies.set(factory, policy);
    }
    pending = html;
    try {
      return policy.createHTML(html);
    } finally {
      pending = null;
    }
  }
}
