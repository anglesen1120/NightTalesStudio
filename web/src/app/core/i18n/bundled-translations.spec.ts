import coreEn from '../../../assets/i18n/core/en.json';
import coreVi from '../../../assets/i18n/core/vi.json';
import publicEn from '../../../assets/i18n/public/en.json';
import publicVi from '../../../assets/i18n/public/vi.json';
import { PRERENDER_SAFE_SCOPES, bundledFor } from './bundled-translations';

describe('bundled translations', () => {
  it('lists exactly the scopes that are bundled, each for both languages', () => {
    expect([...PRERENDER_SAFE_SCOPES]).toEqual(['core', 'public']);
    for (const scope of PRERENDER_SAFE_SCOPES) {
      expect(bundledFor(scope, 'vi')).toBeDefined();
      expect(bundledFor(scope, 'en')).toBeDefined();
    }
    expect(bundledFor('core', 'vi')).toBe(coreVi);
    expect(bundledFor('core', 'en')).toBe(coreEn);
    expect(bundledFor('public', 'vi')).toBe(publicVi);
    expect(bundledFor('public', 'en')).toBe(publicEn);
  });

  it.each(['__proto__', 'constructor', 'toString', 'hasOwnProperty', 'series', '../..'])(
    'finds no bundle for scope %j',
    (scope) => {
      expect(bundledFor(scope, 'vi')).toBeUndefined();
    },
  );

  it.each(['__proto__', 'constructor', 'toString', 'fr'])('finds no bundle for language %j', (lang) => {
    expect(bundledFor('core', lang)).toBeUndefined();
  });
});
