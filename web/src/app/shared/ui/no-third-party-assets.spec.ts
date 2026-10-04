import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

function sourceFiles(root: string): string[] {
  return readdirSync(root).flatMap((entry) => {
    const path = join(root, entry);
    return statSync(path).isDirectory() ? sourceFiles(path) : /\.(html|scss|ts)$/.test(path) && !path.endsWith('.spec.ts') ? [path] : [];
  });
}

describe('shared UI assets', () => {
  it('contains no remote font, icon, or stylesheet reference', () => {
    const root = join(process.cwd(), 'src/app/shared/ui');
    const source = sourceFiles(root).map((path) => readFileSync(path, 'utf8')).join('\n');
    expect(source).not.toMatch(/fonts\.googleapis|fonts\.gstatic|@import\s+url\(https?:|<link[^>]+https?:/i);
    expect(source).not.toMatch(/MatIconRegistry|bypassSecurityTrustResourceUrl/);
  });
});
