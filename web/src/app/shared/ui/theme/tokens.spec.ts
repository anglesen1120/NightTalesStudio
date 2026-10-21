import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

const themeRoot = join(process.cwd(), 'src/app/shared/ui/theme');
const source = readFileSync(join(themeRoot, '_tokens.scss'), 'utf8');
const block = (selector: string) => source.match(new RegExp(`${selector.replace(/[.*+?^${}()|[\\]\\]/g, '\\$&')}\\s*\\{([\\s\\S]*?)\\n\\}`, 'm'))?.[1] ?? '';
const colors = (text: string) => new Map([...text.matchAll(/(--nts-color-[\w-]+):\s*(#[0-9a-f]{6})/gi)].map((match) => [match[1], match[2]]));

function luminance(hex: string): number {
  const values = [1, 3, 5].map((offset) => Number.parseInt(hex.slice(offset, offset + 2), 16) / 255)
    .map((value) => value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4);
  return 0.2126 * values[0]! + 0.7152 * values[1]! + 0.0722 * values[2]!;
}
function contrast(a: string, b: string): number {
  const [light, dark] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (light! + 0.05) / (dark! + 0.05);
}

describe('theme tokens', () => {
  const dark = colors(block("\\[data-theme='dark'\\]"));
  const light = colors(block("\\[data-theme='light'\\]"));

  it('defines identical concrete color keys in both themes', () => {
    expect([...light.keys()].sort()).toEqual([...dark.keys()].sort());
  });

  for (const [name, theme] of [['dark', dark], ['light', light]] as const) {
    it(`${name} meets body and focus contrast thresholds`, () => {
      expect(contrast(theme.get('--nts-color-on-surface')!, theme.get('--nts-color-bg')!)).toBeGreaterThanOrEqual(4.5);
      expect(contrast(theme.get('--nts-color-focus')!, theme.get('--nts-color-bg')!)).toBeGreaterThanOrEqual(3);
    });
  }

  it('ships the Vietnamese subset and approved weights', () => {
    const typography = readFileSync(join(themeRoot, '_typography.scss'), 'utf8');
    for (const subset of ['vietnamese', 'latin-ext', 'latin']) for (const weight of [400, 500, 600]) {
      expect(typography).toContain(`${subset}-${weight}.css`);
    }
    expect('Tiếng Việt: chuyện kể ở Phòng thu').toMatch(/[ếệòư]/);
  });
});
