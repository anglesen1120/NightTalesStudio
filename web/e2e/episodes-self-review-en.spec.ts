import { describeCsp, expect, test } from './fixtures/csp.fixture';
import {
  selfReviewA11yFlow, selfReviewBlockedVariantFlow, selfReviewEmptyAuditFlow, selfReviewNarrow360Flow,
  selfReviewNoExtraRequestFlow, selfReviewNoSourcesFlow, selfReviewPanelFlow, selfReviewReorderFlow,
  selfReviewUnavailableFlow,
} from './fixtures/story-editor-stubs';

// English parity for 10.5.3-T2: the vi spec owns the same matrix through the same fixture flows, so this file only
// differs in the active language — every copy assertion resolves from the shipped `en` bundles.
test.use({ locale: 'en-US', timezoneId: 'UTC' });

test.describe('self-review, revised badge and similarity highlights (en, stubbed)', () => {
  test('RevisedVersion_BadgeSelfReviewPanelCountsScoreTableDriftWhyAndSimilarityNote', async ({ page }) => {
    await selfReviewPanelFlow(page, 'en');
  });

  test('SimilarityBlockedHint_ShowsBlockedCopyInlineAndInThePanel', async ({ page }) => {
    await selfReviewBlockedVariantFlow(page, 'en');
  });

  test('NoAuditReport_EmptyStateNoBadgeNoError', async ({ page }) => {
    await selfReviewEmptyAuditFlow(page, 'en');
  });

  test('NullSimilarityScoreWithSources_ShowsUnavailableAndMarksNothing', async ({ page }) => {
    await selfReviewUnavailableFlow(page, 'en');
  });

  test('NoSources_RendersNoSimilarityUi', async ({ page }) => {
    await selfReviewNoSourcesFlow(page, 'en');
  });

  test('Reorder_KeepsTheSimilarityNoteOnTheMovedText', async ({ page }) => {
    await selfReviewReorderFlow(page, 'en');
  });

  test('AxeCleanWithScoreTableAndWhyPanel_AndNoCspOrTrustedTypesConsoleError', async ({ page, cspLog }) => {
    await selfReviewA11yFlow(page, 'en');
    expect(cspLog, `CSP / Trusted Types violation(s) recorded:\n${describeCsp(cspLog)}`).toEqual([]);
  });

  test('Narrow360_PanelHasNoHorizontalOverflow', async ({ page }) => {
    await selfReviewNarrow360Flow(page, 'en');
  });

  test('Panel_RendersFromTheLoadedDetailWithoutAnExtraRequest', async ({ page }) => {
    await selfReviewNoExtraRequestFlow(page, 'en');
  });
});
