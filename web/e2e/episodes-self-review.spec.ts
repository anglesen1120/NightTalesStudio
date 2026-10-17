import { describeCsp, expect, test } from './fixtures/csp.fixture';
import {
  selfReviewA11yFlow, selfReviewBlockedVariantFlow, selfReviewEmptyAuditFlow, selfReviewNarrow360Flow,
  selfReviewNoExtraRequestFlow, selfReviewNoSourcesFlow, selfReviewPanelFlow, selfReviewReorderFlow,
  selfReviewUnavailableFlow,
} from './fixtures/story-editor-stubs';

// 10.5.3-T2 (README D9/D10/D11/D16/D18; task file §Acceptance checks 1-3, 5-12): backend-free proof that the model's
// self-review, the "automatically corrected once" badge and the similarity highlights render from the detail payload
// the editor already loads. The fixture stubs one `source = revised` version with a blocking objective issue, an
// advisory taste issue, an applied revision with a drift finding and one overlapping source segment; the en spec runs
// the same flows. Real compose/mock-mpt acceptance stays with 11.5.1-T1.
test.use({ locale: 'vi-VN', timezoneId: 'UTC' });

test.describe('self-review, revised badge and similarity highlights (vi, stubbed)', () => {
  test('RevisedVersion_BadgeSelfReviewPanelCountsScoreTableDriftWhyAndSimilarityNote', async ({ page }) => {
    await selfReviewPanelFlow(page, 'vi');
  });

  test('SimilarityBlockedHint_ShowsBlockedCopyInlineAndInThePanel', async ({ page }) => {
    await selfReviewBlockedVariantFlow(page, 'vi');
  });

  test('NoAuditReport_EmptyStateNoBadgeNoError', async ({ page }) => {
    await selfReviewEmptyAuditFlow(page, 'vi');
  });

  test('NullSimilarityScoreWithSources_ShowsUnavailableAndMarksNothing', async ({ page }) => {
    await selfReviewUnavailableFlow(page, 'vi');
  });

  test('NoSources_RendersNoSimilarityUi', async ({ page }) => {
    await selfReviewNoSourcesFlow(page, 'vi');
  });

  test('Reorder_KeepsTheSimilarityNoteOnTheMovedText', async ({ page }) => {
    await selfReviewReorderFlow(page, 'vi');
  });

  test('AxeCleanWithScoreTableAndWhyPanel_AndNoCspOrTrustedTypesConsoleError', async ({ page, cspLog }) => {
    await selfReviewA11yFlow(page, 'vi');
    expect(cspLog, `CSP / Trusted Types violation(s) recorded:\n${describeCsp(cspLog)}`).toEqual([]);
  });

  test('Narrow360_PanelHasNoHorizontalOverflow', async ({ page }) => {
    await selfReviewNarrow360Flow(page, 'vi');
  });

  test('Panel_RendersFromTheLoadedDetailWithoutAnExtraRequest', async ({ page }) => {
    await selfReviewNoExtraRequestFlow(page, 'vi');
  });
});
