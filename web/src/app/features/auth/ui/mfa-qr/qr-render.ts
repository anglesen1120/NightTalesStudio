import { toDataURL } from 'qrcode';

/**
 * ESM seam around `qrcode` for the lazy QR chunk (D13/FE14 keep the package out of the initial bundle).
 *
 * `qrcode` 1.5.4 is CommonJS with no ESM build, so `mfa-qr.component.ts` may only reach it through a dynamic import.
 * Dynamically importing the package itself does not work: esbuild gives that namespace a `default` member in the dev
 * server and the production build tree-shakes it down to a module with no members at all, so
 * `const { toDataURL } = await import('qrcode')` threw `toDataURL is not a function`. This module imports the package
 * statically — esbuild resolves the CommonJS interop at build time — and re-exports the function, so the lazy chunk
 * has a real named export in every configuration (`ng serve` and the production build alike). A re-export is not
 * tree-shaken here because the dynamic importer uses it: verify with `grep -o 'export{[^}]*}' dist/web/browser/chunk-*.js`.
 */
export { toDataURL };
