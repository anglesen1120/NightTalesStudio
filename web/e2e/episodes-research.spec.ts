import type { Page } from '@playwright/test';
import { expect, test } from './fixtures/csp.fixture';
import { expectA11yClean, waitForOverlayAnimationEnd } from './fixtures/axe';
import { EPISODE_ID, stubEpisodes } from './fixtures/episodes-stubs';
import { RESEARCH_PATH, researchSheet, researchSource, stubResearch, type ResearchServer } from './fixtures/research-stubs';
import { stubStoryEditor } from './fixtures/story-editor-stubs';
import episodesEn from '../src/assets/i18n/episodes/en.json' with {type:'json'};
import episodesVi from '../src/assets/i18n/episodes/vi.json' with {type:'json'};
import errorsEn from '../src/assets/i18n/errors/en.json' with {type:'json'};
import errorsVi from '../src/assets/i18n/errors/vi.json' with {type:'json'};
import type { E2eLang } from './fixtures/series-stubs';
const copy={en:episodesEn,vi:episodesVi};
async function open(page:Page,lang:E2eLang,server?:ResearchServer){
  await stubEpisodes(page,{lang,topic:'true_crime'});
  const state=await stubResearch(page,server);
  await page.goto(RESEARCH_PATH);
  await expect(page.getByRole('heading',{name:copy[lang].research.title,exact:true})).toBeVisible();
  await page.locator('.fact-sheet-slot').scrollIntoViewIfNeeded();
  await expect(page.locator('nts-fact-sheet-panel')).toBeVisible();
  return state;
}
const seeded=():ResearchServer=>{const sources=[researchSource(1),researchSource(2)];return {sources,sheet:researchSheet(sources),generating:false,requests:[]};};
for(const lang of ['vi','en'] as const){
 test.describe('research '+lang,()=>{
  test.use({locale:lang==='vi'?'vi-VN':'en-US',timezoneId:'UTC'});
  test('fresh null sheet: add link and note, blocked-domain error, manual fetch refresh, gate and a11y',async({page})=>{
   await page.setViewportSize({width:360,height:900});const server=await open(page,lang);const c=copy[lang];
   await expect(page.locator('.gate[role=status]')).toContainText(c.research.gate.writeStoryBlocked);await expect(page.getByText(c.factSheet.empty,{exact:true})).toBeVisible();await expect(page.locator('nts-error-state')).toHaveCount(0);await expectA11yClean(page);
   await page.getByRole('textbox',{name:c.research.sources.urlLabel,exact:true}).fill('https://blocked.invalid/story');await page.getByRole('button',{name:c.research.sources.add,exact:true}).click();
   await expect(page.locator('.field-error')).toContainText((lang==='vi'?errorsVi:errorsEn).source_domain_not_allowed);
   await page.getByRole('textbox',{name:c.research.sources.urlLabel,exact:true}).fill('https://vnexpress.net/story');await page.getByRole('button',{name:c.research.sources.add,exact:true}).click();await expect(page.locator('.source-status')).toContainText(c.research.status.pending);
   server.sources=server.sources.map(source=>({...source,status:'fetched'}));await page.getByRole('button',{name:c.research.sources.refresh,exact:true}).click();await expect(page.locator('.source-status')).toContainText(c.research.status.fetched);
   await page.getByRole('button',{name:c.research.sources.noteLabel,exact:true}).click();await page.getByRole('textbox',{name:c.research.sources.noteLabel,exact:true}).fill('My own note');await page.getByRole('button',{name:c.research.sources.addNote,exact:true}).click();await expect(page.locator('.source-list')).toContainText(c.factSheet.userNoteLabel);
   await expect(page.getByRole('button',{name:c.factSheet.generate,exact:true})).toBeEnabled();await expectA11yClean(page);expect(await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth)).toBe(true);
  });
  test('build, edit/add/delete, save If-Match, approve and unlock editor',async({page})=>{
   const c=copy[lang];const server=seeded();server.sheet=null;await open(page,lang,server);await page.getByRole('button',{name:c.factSheet.generate,exact:true}).click();
   await expect.poll(()=>server.requests.filter(request=>request.url().endsWith('/fact-sheet/generate')).length).toBe(1);expect(server.requests.find(request=>request.url().endsWith('/fact-sheet/generate'))?.headers()['idempotency-key']).toBeTruthy();
   server.generating=false;server.sheet=researchSheet(server.sources);await page.getByRole('button',{name:c.research.sources.refresh,exact:true}).click();const panel=page.locator('nts-fact-sheet-panel');await panel.scrollIntoViewIfNeeded();await expect(panel.locator('textarea')).toHaveCount(1);
   await panel.locator('textarea').first().fill('Edited verified fact.');await page.getByRole('button',{name:c.research.facts.add,exact:true}).click();await expect(panel.locator('textarea')).toHaveCount(2);await panel.locator('textarea').nth(1).fill('Temporary fact');await page.getByRole('button',{name:c.research.facts.deleteLabel.replace('{{index}}','2'),exact:true}).click();await expect(panel.locator('textarea')).toHaveCount(1);
   await page.getByRole('button',{name:c.factSheet.save,exact:true}).click();await expect(page.getByRole('button',{name:c.factSheet.approve,exact:true})).toBeEnabled();await page.getByRole('button',{name:c.factSheet.approve,exact:true}).click();await expect(page.locator('.gate')).toContainText(c.research.gate.open);await expect(panel.locator('textarea')).toHaveCount(0);await expectA11yClean(page);
   await stubStoryEditor(page,lang);await stubResearch(page,server);await page.getByRole('link',{name:c.research.backToStory,exact:true}).click();await expect(page).toHaveURL(new RegExp('/episodes/'+EPISODE_ID+'$'));await expect(page.getByRole('button',{name:c.editor.action.generate_story,exact:true})).toBeEnabled();await expectA11yClean(page);
  });
  test('second tab stale save preserves draft and resolves via save as new',async({page,context})=>{
   const c=copy[lang];const server=seeded();await open(page,lang,server);await page.locator('nts-fact-sheet-panel').scrollIntoViewIfNeeded();await expect(page.locator('nts-fact-row textarea')).toHaveCount(1);
   const second=await context.newPage();await open(second,lang,server);await second.locator('nts-fact-sheet-panel').scrollIntoViewIfNeeded();await second.locator('nts-fact-row textarea').fill('Second tab draft');
   await page.locator('nts-fact-row textarea').fill('First tab draft');await page.getByRole('button',{name:c.factSheet.save,exact:true}).click();await expect.poll(()=>server.sheet?.version).toBe(2);
   await second.getByRole('button',{name:c.factSheet.save,exact:true}).click();const dialog=second.getByRole('dialog');await expect(dialog).toBeVisible();await waitForOverlayAnimationEnd(second);await expectA11yClean(second);await expect(second.locator('nts-fact-row textarea')).toHaveValue('Second tab draft');
   await dialog.getByRole('button',{name:(lang==='vi'?errorsVi:errorsEn).conflict.saveAsNew,exact:true}).click();await expect(dialog).toHaveCount(0);await expect.poll(()=>server.sheet?.version).toBe(3);await expect(second.locator('nts-fact-row textarea')).toHaveValue('Second tab draft');await expectA11yClean(second);await second.close();
  });
  test('one fetched cited URL is refused on approve with the closed gate reason', async ({page}) => {
    const c = copy[lang], server = seeded();
    server.sources = [researchSource(1)];
    server.sheet = researchSheet(server.sources);
    await open(page, lang, server);
    await page.getByRole('button', {name:c.factSheet.approve, exact:true}).click();
    await expect(page.locator('nts-fact-sheet-panel [role=alert]')).toContainText((lang==='vi'?errorsVi:errorsEn).story_sources_required);
    await expect(page.locator('.gate[role=status]')).toContainText(c.research.gate.writeStoryBlocked);
    await expectA11yClean(page);
  });
  test('untrusted title and unsafe URL stay inert',async({page})=>{
   const server=seeded();server.sources=[researchSource(1,{title:'<img src=x onerror=alert(1)>{{title}}',url:'javascript:alert(1)',registrableDomain:'javascript:alert(1)'})];server.sheet=researchSheet(server.sources);await open(page,lang,server);
   await expect(page.locator('nts-source-row')).toContainText('<img src=x onerror=alert(1)>{{title}}');await expect(page.locator('nts-source-row img, nts-source-row a')).toHaveCount(0);await page.locator('nts-fact-sheet-panel').scrollIntoViewIfNeeded();await expect(page.locator('nts-fact-row')).toBeVisible();await expect(page.locator('nts-fact-row a')).toHaveCount(0);await expectA11yClean(page);
  });
 });
}
