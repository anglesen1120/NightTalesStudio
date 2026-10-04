import { rmSync } from 'node:fs';
import { AUTH_DIR } from './fixtures/auth';

/** Removes the enrolled owner TOTP secret at the end of every run, failed or not (WR14). Prints nothing about it. */
export default function globalTeardown(): void {
  rmSync(AUTH_DIR, { recursive: true, force: true });
}
