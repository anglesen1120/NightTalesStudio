import { test } from './fixtures/csp.fixture';
import {
  beforeUnloadCleanFlow, beforeUnloadDirtyFlow, deleteUndoFlow, deleteWithoutSavingReloadFlow, dirtyGuardFlow,
  keyboardReorderFlow, logoutDirtyFlow, mobileReorderFlow, mouseDragFlow, narrow360ActionWrapFlow, reducedMotionFlow,
  saveAfterMoveFlow, structureAdvisoryFlow,
} from './fixtures/story-editor-stubs';

// English parity for 10.5.9-T1: the same scenarios as `episodes-structure.spec.ts` (vi) run through the shared flows.
// The one deliberate omission is the real 10 s undo-window expiry of bullet 3 (the delete + snackbar + Undo restore
// are still covered here): that wait asserts locale-independent store state and is exercised once, in the vi spec.
test.use({ locale: 'en-US', timezoneId: 'UTC' });

test.describe('story structure (en, stubbed)', () => {
  test('KeyboardReorder_Paragraph2UpWithEnterThenDownWithSpace_AnnouncesPositionsAndReturnsFocus_EndsAriaDisabled', async ({ page }) => {
    await keyboardReorderFlow(page, 'en');
  });

  test('SaveAfterMove_PutBodyCarriesNewParagraphOrderWithIfMatchAndNoContentWrapper_ReloadReflectsIt', async ({ page }) => {
    await saveAfterMoveFlow(page, 'en');
  });

  test('MouseDrag_HandleReordersParagraph_SaveSendsNewOrder', async ({ page }) => {
    await mouseDragFlow(page, 'en');
  });

  test('DeleteUndo_SnackbarShowsDeletedCopyAndUndo_RestoresPositionKeywordsBeatAndFocus', async ({ page }) => {
    await deleteUndoFlow(page, 'en', { waitForExpiry: false });
  });

  test('DirtyGuard_CleanDraftNavigatesWithoutDialog_DirtyDraftStayKeepsThenLeaveDiscards', async ({ page }) => {
    await dirtyGuardFlow(page, 'en');
  });

  test('BeforeUnload_DirtyDraftPromptsOnTabClose', async ({ page }) => {
    await beforeUnloadDirtyFlow(page, 'en');
  });

  test('BeforeUnload_CleanDraftAfterSaveShowsNoPrompt', async ({ page }) => {
    await beforeUnloadCleanFlow(page, 'en');
  });

  test('LogoutDirty_AsksFirst_StayKeepsTheSession_LeaveSignsOut', async ({ page }) => {
    await logoutDirtyFlow(page, 'en');
  });

  test('StructureAdvisory_VisibleAndAxeCleanAcrossADelete_KeepsSaveEnabled', async ({ page }) => {
    await structureAdvisoryFlow(page, 'en');
  });

  test('Mobile360_NoHorizontalScroll_EveryParagraphAndControlReachableByKeyboard', async ({ page }) => {
    await mobileReorderFlow(page, 'en');
  });

  test('ReducedMotion_DisablesDropListAndSnackbarMotionCSS', async ({ page }) => {
    await reducedMotionFlow(page, 'en');
  });

  test('DeleteWithoutSaving_Reload_RestoresTheSavedVersion', async ({ page }) => {
    await deleteWithoutSavingReloadFlow(page, 'en');
  });

  test('Narrow360_ActionButtonsWrapUnderTheTextarea', async ({ page }) => {
    await narrow360ActionWrapFlow(page, 'en');
  });
});
