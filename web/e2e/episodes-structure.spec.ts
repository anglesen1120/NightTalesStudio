import { test } from './fixtures/csp.fixture';
import {
  beforeUnloadCleanFlow, beforeUnloadDirtyFlow, deleteUndoFlow, dirtyGuardFlow, keyboardReorderFlow, logoutDirtyFlow,
  mobileReorderFlow, mouseDragFlow, reducedMotionFlow, saveAfterMoveFlow, structureAdvisoryFlow,
} from './fixtures/story-editor-stubs';

// 10.5.9-T1 (README D16/D18; task file §Acceptance checks and §Tests "E2E"): backend-free structure proof sharing
// the session, SignalR, axe and CSP/Trusted Types fixtures with the generation, list and editor specs. Real
// compose/mock-mpt acceptance stays with 11.5.1-T1. One named test per acceptance bullet; the en spec reuses these
// same flows, and only the one real 10 s undo-window wait is kept here (it asserts locale-independent store state).
test.use({ locale: 'vi-VN', timezoneId: 'UTC' });

test.describe('story structure (vi, stubbed)', () => {
  test('KeyboardReorder_Paragraph2UpWithEnterThenDownWithSpace_AnnouncesPositionsAndReturnsFocus_EndsAriaDisabled', async ({ page }) => {
    await keyboardReorderFlow(page, 'vi');
  });

  test('SaveAfterMove_PutBodyCarriesNewParagraphOrderWithIfMatchAndNoContentWrapper_ReloadReflectsIt', async ({ page }) => {
    await saveAfterMoveFlow(page, 'vi');
  });

  test('MouseDrag_HandleReordersParagraph_SaveSendsNewOrder', async ({ page }) => {
    await mouseDragFlow(page, 'vi');
  });

  test('DeleteUndo_SnackbarShowsDeletedCopyAndUndo_RestoresPositionKeywordsBeatAndFocus_ThenExpiryClearsTheUndoAction', async ({ page }) => {
    test.setTimeout(60_000);
    await deleteUndoFlow(page, 'vi', { waitForExpiry: true });
  });

  test('DirtyGuard_CleanDraftNavigatesWithoutDialog_DirtyDraftStayKeepsThenLeaveDiscards', async ({ page }) => {
    await dirtyGuardFlow(page, 'vi');
  });

  test('BeforeUnload_DirtyDraftPromptsOnTabClose', async ({ page }) => {
    await beforeUnloadDirtyFlow(page, 'vi');
  });

  test('BeforeUnload_CleanDraftAfterSaveShowsNoPrompt', async ({ page }) => {
    await beforeUnloadCleanFlow(page, 'vi');
  });

  test('LogoutDirty_AsksFirst_StayKeepsTheSession_LeaveSignsOut', async ({ page }) => {
    await logoutDirtyFlow(page, 'vi');
  });

  test('StructureAdvisory_VisibleAndAxeCleanAcrossADelete_KeepsSaveEnabled', async ({ page }) => {
    await structureAdvisoryFlow(page, 'vi');
  });

  test('Mobile360_NoHorizontalScroll_EveryParagraphAndControlReachableByKeyboard', async ({ page }) => {
    await mobileReorderFlow(page, 'vi');
  });

  test('ReducedMotion_DisablesDropListAndSnackbarMotionCSS', async ({ page }) => {
    await reducedMotionFlow(page, 'vi');
  });
});
