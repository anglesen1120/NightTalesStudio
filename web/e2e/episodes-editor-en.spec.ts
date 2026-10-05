import { test } from './fixtures/csp.fixture';
import { mobileSegmentsFlow, saveReloadFlow } from './fixtures/story-editor-stubs';

// English main-flow/a11y/mobile parity; vi spec owns the full negative and navigation matrix.
// Both reuse the existing session + SignalR + CSP/Trusted Types + axe fixture footprint.
test.use({ locale: 'en-US', timezoneId: 'UTC' });

test.describe('story editor (en, stubbed)', () => {
  test('EditTitleHookParagraphKeyword_SaveExactlyOneFullStoryWithIfMatch_ReloadsSavedVersion', async ({ page }) => {
    await saveReloadFlow(page, 'en');
  });

  test('Mobile360_AllSegmentsReachableByNamedSelector_AxeAndNoHorizontalScroll', async ({ page }) => {
    await mobileSegmentsFlow(page, 'en');
  });
});
