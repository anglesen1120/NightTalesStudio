import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import { researchTranslations, source } from '../../testing/research-ui.testing';
import { SourceRowComponent } from './source-row.component';
afterEach(() => TestBed.resetTestingModule());
async function setup() {
  await TestBed.configureTestingModule({ imports: [SourceRowComponent, researchTranslations()], providers: [...ZONELESS_TEST_PROVIDERS] }).compileComponents();
  const fixture = TestBed.createComponent(SourceRowComponent); fixture.componentRef.setInput('source', source()); await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement };
}
describe('SourceRowComponent', () => {
  it.each(['pending', 'fetched', 'failed', 'rejected'] as const)('maps %s with icon and text; only failed offers retry', async status => {
    const { fixture, root } = await setup(); fixture.componentRef.setInput('source', source({ status, errorCode: status === 'rejected' ? 'source_domain_not_allowed' : null })); await fixture.whenStable();
    expect(root.querySelector('.source-status')?.textContent?.trim()).toBe(({pending:'Pending', fetched:'Fetched', failed:'Failed', rejected:'Blocked'})[status]);
    expect(root.querySelector('mat-spinner, nts-icon-check, nts-icon-alert')).not.toBeNull();
    expect([...root.querySelectorAll('button')].some(button => button.textContent?.trim() === 'Retry')).toBe(status === 'failed');
  });
  it('renders untrusted title literally and never links unsafe URLs', async () => {
    const { fixture, root } = await setup(); const title = '<img src=x onerror=alert(1)>{{title}}';
    fixture.componentRef.setInput('source', source({ title })); await fixture.whenStable();
    expect(root.textContent).toContain(title); expect(root.querySelector('img')).toBeNull(); expect(root.querySelector('a')?.getAttribute('rel')).toBe('noopener noreferrer');
    fixture.componentRef.setInput('source', source({ title, url: 'javascript:alert(1)' })); await fixture.whenStable(); expect(root.querySelector('a')).toBeNull();
  });
  it('labels notes without rendering long note text', async () => {
    const { fixture, root } = await setup(); fixture.componentRef.setInput('source', source({ kind:'user_note', noteText:'secret-note', url:null })); await fixture.whenStable();
    expect(root.textContent).toContain('your note'); expect(root.textContent).not.toContain('secret-note');
  });
  it.each(['source_url_invalid','source_domain_not_allowed','source_unreachable','source_too_large','source_blocked_by_robots','source_unsupported_type','source_no_text'])('translates the fetch reason %s in the episodes scope',async errorCode=>{
    const {fixture,root}=await setup();fixture.componentRef.setInput('source',source({status:'failed',errorCode}));await fixture.whenStable();
    expect(root.querySelector('.failure')?.textContent?.trim()).toBeTruthy();expect(root.querySelector('.failure')?.textContent).not.toContain('sources.error.');
  });
  it.each([null,'unknown_server_code'])('uses a translated fallback for %s',async errorCode=>{
    const {fixture,root}=await setup();fixture.componentRef.setInput('source',source({status:'failed',errorCode}));await fixture.whenStable();
    expect(root.querySelector('.failure')?.textContent).toContain('This source could not be fetched.');
  });
});
