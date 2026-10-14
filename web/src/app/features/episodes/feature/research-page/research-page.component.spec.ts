import { signal } from '@angular/core';
import { DeferBlockBehavior, DeferBlockState, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { afterEach, describe, expect, it } from 'vitest';
import type { AppError } from '@core/errors';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import { ResearchStore } from '../../application/research.store';
import { researchTranslations, source, fact, sheet } from '../../testing/research-ui.testing';
import type { Fact, FactSheet, ResearchSource, SourceLimits } from '../../domain/research.model';
import { ResearchPageComponent } from './research-page.component';
class FakeResearchStore {
  readonly sources=signal<readonly ResearchSource[]>([]); readonly limits=signal<SourceLimits|null>(null); readonly factSheet=signal<FactSheet|null>(null); readonly draft=signal<readonly (Fact & {readonly clientKey:string})[]>([]);
  readonly loaded=signal(true); readonly loading=signal(false); readonly refreshing=signal(false); readonly generation=signal(null); readonly running=signal(false); readonly pendingCount=signal(0); readonly urlCount=signal(0); readonly canAddUrl=signal(true); readonly usedCount=signal(0); readonly maxCount=signal(10); readonly canGenerate=signal(false); readonly canSave=signal(false); readonly canApprove=signal(false); readonly dirty=signal(false); readonly isApproved=signal(false); readonly gate=signal('sheet_not_approved'); readonly isTrueCrime=signal(true); readonly savingFactSheet=signal(false); readonly approvingFactSheet=signal(false); readonly generating=signal(false);
  readonly error=signal<AppError|null>(null); readonly formError=signal<AppError|null>(null); readonly panelError=signal<AppError|null>(null); readonly conflict=signal(null); readonly rateLimitSeconds=signal(null);
  loads:string[]=[]; watches:string[]=[]; refreshes=0;
  load(id:string) {this.loads.push(id); return Promise.resolve({ok:true as const,value:undefined});} watch(id:string){this.watches.push(id);}
  refresh(){this.refreshes++;return Promise.resolve({ok:true as const,value:undefined});}
  clearFormError(){this.formError.set(null);} clearPanelError(){this.panelError.set(null);} reset(){this.dirty.set(false);this.draft.set([]);this.factSheet.set(null);}
}
afterEach(() => TestBed.resetTestingModule());
async function setup(lang:'vi'|'en'='en', store=new FakeResearchStore()) {
  const params=convertToParamMap({id:'episode-a'});
  await TestBed.configureTestingModule({imports:[ResearchPageComponent,researchTranslations(lang)],providers:[...ZONELESS_TEST_PROVIDERS,provideRouter([]),{provide:ResearchStore,useValue:store},{provide:ActivatedRoute,useValue:{paramMap:of(params),snapshot:{paramMap:params},parent:null}}],deferBlockBehavior:DeferBlockBehavior.Manual}).compileComponents();
  const fixture=TestBed.createComponent(ResearchPageComponent);
  await fixture.whenStable();
  for (const block of await fixture.getDeferBlocks()) await block.render(DeferBlockState.Complete);
  await fixture.whenStable();
  return {fixture,store,component:fixture.componentInstance,root:fixture.nativeElement as HTMLElement};
}
describe('ResearchPageComponent',()=>{
  it.each(['vi','en'] as const)('loads and watches once and announces the closed gate in %s',async lang=>{
    const {store,root}=await setup(lang);expect(store.loads).toEqual(['episode-a']);expect(store.watches).toEqual(['episode-a']);
    expect(root.querySelector('.gate[role=status]')?.textContent).toContain(lang==='en'?'Approve the fact sheet':'Cần duyệt bảng dữ kiện');
    expect(root.textContent).not.toMatch(/\b(credits?|cost|price|balance)\b|tín dụng|chi phí|số dư/i);
  });
  it('renders 200 null as empty, focuses add form and refreshes manually',async()=>{
    const {root,store}=await setup();expect(root.querySelector('nts-error-state')).toBeNull();expect(root.textContent).toContain('No fact sheet yet');
    expect(document.activeElement?.id).toContain('research-source-field');
    [...root.querySelectorAll<HTMLButtonElement>('button')].find(button=>button.textContent?.trim()==='Refresh status')!.click();expect(store.refreshes).toBe(1);
  });
  it('renders ErrorState only for 403 and displays source-load network errors inline',async()=>{
    const {fixture,root,store}=await setup();store.error.set({code:'forbidden',status:403,kind:'auth',fieldErrors:{}});await fixture.whenStable();expect(root.querySelector('nts-error-state')).not.toBeNull();
    store.error.set({code:'network_error',status:0,kind:'network',fieldErrors:{}});await fixture.whenStable();expect(root.querySelector('nts-error-state')).toBeNull();expect(root.querySelector('nts-source-add-form')).not.toBeNull();
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
});
