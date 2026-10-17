import { signal } from '@angular/core';
import { DeferBlockBehavior, DeferBlockState, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AppError } from '@core/errors';
import { SessionStore } from '@core/session';
import { RealtimeService } from '@core/realtime';
import { SourceFinderStore } from '../../application/source-finder.store';
import { SourceFinderRepository } from '../../domain/source-finder.repository';
import { FakeSourceFinderRepository } from '../../testing/fake-source-finder.repository';
import { ConflictDialog, type ConflictDialogData } from '@shared/ui';
import { EPISODE_GATE } from '../../application/episode-gate';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import { ResearchStore } from '../../application/research.store';
import { researchCopy, researchCopies, researchTranslations, source, fact, sheet } from '../../testing/research-ui.testing';
import type { Fact, FactSheet, ResearchSource, SourceLimits } from '../../domain/research.model';
import type { EpisodeGeneration } from '../../domain/episode.model';
import { ResearchPageComponent } from './research-page.component';
class FakeResearchStore {
  readonly sources=signal<readonly ResearchSource[]>([]); readonly limits=signal<SourceLimits|null>(null); readonly factSheet=signal<FactSheet|null>(null); readonly draft=signal<readonly (Fact & {readonly clientKey:string})[]>([]);
  readonly loaded=signal(true); readonly loading=signal(false); readonly refreshing=signal(false); readonly generation=signal<EpisodeGeneration|null>(null); readonly running=signal(false); readonly pendingCount=signal(0); readonly urlCount=signal(0); readonly canAddUrl=signal(true); readonly usedCount=signal(0); readonly maxCount=signal(10); readonly canGenerate=signal(false); readonly canSave=signal(false); readonly canApprove=signal(false); readonly dirty=signal(false); readonly isApproved=signal(false); readonly gate=signal('sheet_not_approved'); readonly isTrueCrime=signal(true); readonly savingFactSheet=signal(false); readonly approvingFactSheet=signal(false); readonly generating=signal(false);
  readonly error=signal<AppError|null>(null); readonly formError=signal<AppError|null>(null); readonly panelError=signal<AppError|null>(null); readonly conflict=signal<{etag:string}|null>(null); readonly rateLimitSeconds=signal<number|null>(null);
  readonly formRateLimitSeconds=signal<number|null>(null); readonly conflictSource=signal<'save'|'approve'|'edit'|null>(null);
  loads:string[]=[]; watches:string[]=[]; refreshes=0;
  load(id:string) {this.loads.push(id); return Promise.resolve({ok:true as const,value:undefined});} watch(id:string){this.watches.push(id);}
  refresh(){this.refreshes++;return Promise.resolve({ok:true as const,value:undefined});}
  clearFormError(){this.formError.set(null);this.formRateLimitSeconds.set(null);} clearFormRateLimit(){this.formError.set(null);this.formRateLimitSeconds.set(null);} clearPanelError(){this.panelError.set(null);this.rateLimitSeconds.set(null);} reset(){this.dirty.set(false);this.draft.set([]);this.factSheet.set(null);}
}
afterEach(() => TestBed.resetTestingModule());
async function setup(lang:'vi'|'en'='en', store=new FakeResearchStore(), features?: readonly string[]) {
  const params=convertToParamMap({id:'episode-a'});
  const dialogs:ConflictDialogData[]=[];
  const finderRepo = new FakeSourceFinderRepository();
  const finderCalls = { find: vi.spyOn(finderRepo, 'find'), candidates: vi.spyOn(finderRepo, 'candidates'), add: vi.spyOn(finderRepo, 'add'), dismiss: vi.spyOn(finderRepo, 'dismiss') };
  const session = { generation: signal(0), hasFeature: (key: string) => features?.includes(key) ?? false };
  await TestBed.configureTestingModule({imports:[ResearchPageComponent,researchTranslations(lang)],providers:[...ZONELESS_TEST_PROVIDERS,provideRouter([]),{provide:SessionStore,useValue:session},{provide:RealtimeService,useValue:{subscribeEpisode:()=>({unsubscribe:()=>undefined})}},{provide:SourceFinderRepository,useValue:finderRepo},{provide:ConflictDialog,useValue:{open:(data:ConflictDialogData)=>{dialogs.push(data);return {afterClosed:()=>of('cancel' as const)};}}},{provide:ActivatedRoute,useValue:{paramMap:of(params),snapshot:{paramMap:params},parent:null}}],deferBlockBehavior:DeferBlockBehavior.Manual})
    .overrideComponent(ResearchPageComponent,{set:{providers:[{provide:ResearchStore,useValue:store},SourceFinderStore,{provide:EPISODE_GATE,useValue:store.gate}]}}).compileComponents();
  const fixture=TestBed.createComponent(ResearchPageComponent);
  await fixture.whenStable();
  for (const block of await fixture.getDeferBlocks()) await block.render(DeferBlockState.Complete);
  await fixture.whenStable();
  return {fixture,store,dialogs,finderRepo,finderCalls,component:fixture.componentInstance,root:fixture.nativeElement as HTMLElement};
}
describe('ResearchPageComponent',()=>{
  it.each([{label:'off',features:[]},{label:'missing',features:undefined}])('has no finder DOM or repository traffic when features are $label',async ({features})=>{
    const {root,finderCalls}=await setup('en',new FakeResearchStore(),features);
    expect(root.querySelector('nts-source-finder-panel')).toBeNull();
    for (const call of Object.values(finderCalls)) expect(call).not.toHaveBeenCalled();
  });
  it('mounts the finder between sources and fact sheet only with the flag on',async()=>{
    const {root,finderCalls}=await setup('en',new FakeResearchStore(),['source_finder']);
    const panel=root.querySelector('nts-source-finder-panel')!;
    expect(panel).not.toBeNull();expect(finderCalls.candidates).toHaveBeenCalledTimes(1);expect(finderCalls.find).not.toHaveBeenCalled();
    expect(root.querySelector('.source-section')!.compareDocumentPosition(panel) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(panel.compareDocumentPosition(root.querySelector('.fact-sheet-slot')!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });
  it('keeps the sources-section running copy visible without a second live announcement',async()=>{
    const store=new FakeResearchStore();
    store.generation.set({kind:'find_sources',status:'running',stage:'queued',errorCode:null,startedAt:null,processingPaused:null});
    const {root}=await setup('en',store,['source_finder']);
    const copies=[...root.querySelectorAll('p')].filter(element=>element.textContent?.trim()===researchCopy.sources.find.running);
    expect(copies).toHaveLength(2);
    const sourceCopy=copies.find(element=>element.closest('.source-section'))!;
    expect(sourceCopy.getAttribute('role')).toBeNull();expect(sourceCopy.closest('[aria-live]')).toBeNull();
    const panelCopy=copies.find(element=>element.closest('nts-source-finder-panel'))!;
    expect(panelCopy.getAttribute('role')).toBe('status');expect(panelCopy.closest('[aria-live]')?.getAttribute('aria-live')).toBe('polite');
  });
  it.each(['vi','en'] as const)('loads and watches once and announces the closed gate in %s',async lang=>{
    const {store,root}=await setup(lang);expect(store.loads).toEqual(['episode-a']);expect(store.watches).toEqual(['episode-a']);
    expect(root.querySelector('.gate[role=status]')?.textContent).toContain(researchCopies[lang].research.gate.writeStoryBlocked);
    expect(root.textContent).not.toMatch(/\b(credits?|cost|price|balance)\b|tín dụng|chi phí|số dư/i);
  });
  it('renders 200 null as empty, focuses add form and refreshes manually',async()=>{
    const {root,store}=await setup();expect(root.querySelector('nts-error-state')).toBeNull();expect(root.textContent).toContain(researchCopy.factSheet.empty);
    expect(document.activeElement?.id).toContain('research-source-field');
    [...root.querySelectorAll<HTMLButtonElement>('button')].find(button=>button.textContent?.trim()===researchCopy.research.sources.refresh)!.click();expect(store.refreshes).toBe(1);
  });
  it('renders access errors as fatal and keeps loaded data on a refresh network error',async()=>{
    const {fixture,root,store}=await setup();store.error.set({code:'forbidden',status:403,kind:'auth',fieldErrors:{}});await fixture.whenStable();expect(root.querySelector('nts-error-state')).not.toBeNull();
    store.error.set({code:'network_error',status:0,kind:'network',fieldErrors:{}});await fixture.whenStable();expect(root.querySelector('nts-error-state')).toBeNull();expect(root.querySelector('nts-source-add-form')).not.toBeNull();
  });
  it('treats a failed initial source load as unavailable and retries load rather than refresh',async()=>{
    const store=new FakeResearchStore();store.loaded.set(false);store.error.set({code:'network_error',status:0,kind:'network',fieldErrors:{}});
    const {root}=await setup('en',store);expect(root.querySelector('nts-error-state')).not.toBeNull();expect(root.querySelector('nts-source-add-form, nts-fact-sheet-panel')).toBeNull();
    root.querySelector<HTMLButtonElement>('nts-error-state button')!.click();expect(store.loads).toEqual(['episode-a','episode-a']);expect(store.refreshes).toBe(0);
  });
  it('uses wire source limits and protects tab navigation even before debounced dirty flips',async()=>{
    const store=new FakeResearchStore();store.sources.set([source()]);store.usedCount.set(1);store.maxCount.set(7);store.factSheet.set(sheet());store.draft.set([fact({statement:'Unsaved'})]);
    const {root,component}=await setup('en',store);expect(root.querySelector('.counter')?.textContent).toContain('1/7');const event=new Event('beforeunload',{cancelable:true});component.beforeUnload(event);expect(event.defaultPrevented).toBe(true);
  });
  it('protects in-flight saves even with a clean or absent sheet',async()=>{
    const store=new FakeResearchStore();store.savingFactSheet.set(true);
    const {component}=await setup('en',store);const event=new Event('beforeunload',{cancelable:true});component.beforeUnload(event);expect(event.defaultPrevented).toBe(true);
    expect(typeof component.confirmLeave).toBe('function');
  });
  it('disables URL additions at the daily limit but still permits notes',async()=>{
    const store=new FakeResearchStore();store.limits.set({maxPerEpisode:10,urlSourcesLast24h:20,maxUrlSourcesPerDay:20});
    const {component}=await setup('en',store);expect(component.canAddUrl()).toBe(false);
  });
  it.each(['save','approve','edit'] as const)('offers save-as-new only for a save conflict, not %s',async source=>{
    const store=new FakeResearchStore();store.conflictSource.set(source);store.conflict.set({etag:'fresh'});
    const {dialogs}=await setup('en',store);expect(dialogs).toHaveLength(1);expect(dialogs[0]?.saveAsNewLabel).toBe(source==='save'?'errors.conflict.saveAsNew':undefined);
  });
  it('renders zero Retry-After and clears both independent waits on elapsed',async()=>{
    const store=new FakeResearchStore();store.rateLimitSeconds.set(0);store.formRateLimitSeconds.set(0);
    await setup('en',store);expect(store.rateLimitSeconds()).toBeNull();expect(store.formRateLimitSeconds()).toBeNull();
  });
});
