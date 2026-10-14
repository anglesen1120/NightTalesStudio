import { describe, expect, it } from 'vitest';
import type { ResearchPageComponent } from '../feature/research-page/research-page.component';
import { dirtyResearchGuard } from './dirty-research.guard';

function guardWith(confirmLeave: () => Promise<boolean>): ResearchPageComponent {
  return { confirmLeave } as unknown as ResearchPageComponent;
}

describe('dirtyResearchGuard (QA)', () => {
  it('lets navigation proceed when the page confirms leaving', async () => {
    let asked = 0;
    const component = guardWith(() => { asked += 1; return Promise.resolve(true); });
    expect(await dirtyResearchGuard(component, {} as never, {} as never, {} as never)).toBe(true);
    expect(asked).toBe(1);
  });

  it('blocks navigation when the page declines leaving', async () => {
    const component = guardWith(() => Promise.resolve(false));
    expect(await dirtyResearchGuard(component, {} as never, {} as never, {} as never)).toBe(false);
  });
});
