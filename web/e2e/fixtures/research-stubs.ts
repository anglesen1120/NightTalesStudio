import type { Page, Request } from '@playwright/test';
import type { FactSheetDto } from '../../src/app/shared/api/models/fact-sheet-dto';
import type { FactItemInputDto } from '../../src/app/shared/api/models/fact-item-input-dto';
import type { SourceListItemDto } from '../../src/app/shared/api/models/source-list-item-dto';
import type { SourceDto } from '../../src/app/shared/api/models/source-dto';
import { EPISODE_ID, episodeDetail } from './episodes-stubs';
import { fulfilJson, problem } from './series-stubs';
import { editorDetail } from './story-editor-stubs';
export const RESEARCH_PATH = '/episodes/' + EPISODE_ID + '/research';
const API = '/api/v1/episodes/' + EPISODE_ID;
export interface ResearchServer { sources: SourceListItemDto[]; sheet: FactSheetDto|null; generating:boolean; requests:Request[]; }
export function researchSource(index:number, overrides:Partial<SourceListItemDto>={}):SourceListItemDto {
 return {id:'source-'+index,kind:'url',url:'https://vnexpress.net/story-'+index,registrableDomain:'vnexpress.net',noteText:null,status:'fetched',errorCode:null,errorMessageKey:null,httpStatus:200,title:'Public record '+index,finalUrl:null,contentType:'text/html',textChars:100,isCited:true,createdAt:'2026-10-14T00:00:00Z',fetchedAt:'2026-10-14T00:01:00Z',...overrides};
}
export function researchSheet(sources:readonly SourceListItemDto[], version=1):FactSheetDto {
 return {id:'sheet-'+version,version,status:'draft',etag:'"fs-sheet-'+version+'"',promptVersion:'facts.v1',createdAt:'2026-10-14T00:00:00Z',approvedAt:null,maxFactId:1,facts:[{id:'f1',statement:'A verified fact.',confidence:'low',sourceOverlap:null,origin:'model',sources:sources.map((source,index)=>({sourceId:source.id,alias:'S'+(index+1),title:source.title??source.registrableDomain??'',url:source.url,isUserNote:source.kind==='user_note'}))}]};
}
function sourceResponse(source:SourceListItemDto):SourceDto {
 return {id:source.id,kind:source.kind,url:source.url,normalizedUrl:source.url,noteText:source.noteText,registrableDomain:source.registrableDomain,status:source.status,errorCode:source.errorCode,httpStatus:source.httpStatus,title:source.title,finalUrl:source.finalUrl,contentType:source.contentType,textChars:source.textChars,contentSha256:null,createdAt:source.createdAt,fetchedAt:source.fetchedAt};
}
export async function stubResearch(page:Page, server:ResearchServer={sources:[],sheet:null,generating:false,requests:[]}):Promise<ResearchServer> {
 await page.route(url=>url.pathname===API, async route=>fulfilJson(route,200,server.generating ? episodeDetail('queued',{generation:{kind:'fact_sheet',status:'running',stage:'extracting',errorCode:null,startedAt:'2026-10-14T00:00:00Z'}}) : editorDetail({nextActions:['generate_story']})));
 await page.route(url=>url.pathname===API+'/sources',async route=>{
  const request=route.request(); server.requests.push(request);
  if(request.method()==='GET') return fulfilJson(route,200,{items:server.sources,limits:{maxPerEpisode:10,urlSourcesLast24h:server.sources.filter(source=>source.kind==='url').length,maxUrlSourcesPerDay:20}});
  const body=request.postDataJSON() as {url?:string;noteText?:string};
  if(body.url?.includes('blocked.invalid')) return fulfilJson(route,400,problem(400,'source_domain_not_allowed'),{'Content-Type':'application/problem+json'});
  const source=researchSource(server.sources.length+1,body.noteText!==undefined ? {kind:'user_note',url:null,title:null,noteText:body.noteText,status:'fetched'} : {url:body.url??null,status:'pending'});server.sources.push(source);return fulfilJson(route,201,sourceResponse(source));
 });
 await page.route(url=>url.pathname.startsWith(API+'/sources/'),async route=>{const request=route.request();server.requests.push(request);const id=new URL(request.url()).pathname.split('/')[6]; if(request.method()==='DELETE'){server.sources=server.sources.filter(source=>source.id!==id);return route.fulfill({status:204});}const index=server.sources.findIndex(source=>source.id===id); const previous=server.sources[index]; if(!previous)return fulfilJson(route,404,problem(404,'source_not_found'),{'Content-Type':'application/problem+json'}); const pending={...previous,status:'pending' as const,errorCode:null};server.sources[index]=pending;return fulfilJson(route,202,sourceResponse(pending));});
 await page.route(url=>url.pathname===API+'/fact-sheet',async route=>{
  const request=route.request();server.requests.push(request);
  if(request.method()==='GET')return fulfilJson(route,200,server.sheet,server.sheet?{ETag:server.sheet.etag}:{});
  if(request.headers()['if-match']!==server.sheet?.etag)return fulfilJson(route,412,problem(412,'fact_sheet_conflict'),{'Content-Type':'application/problem+json'});
  const body=request.postDataJSON() as {content:{facts:FactItemInputDto[]}};const previous=server.sheet;const next=researchSheet(server.sources,Number(previous?.version??0)+1);
  next.facts=body.content.facts.map((input,index)=>({id:input.id??'f'+(index+1),statement:input.statement,confidence:input.confidence??previous?.facts.find(fact=>fact.id===input.id)?.confidence??'high',sourceOverlap:null,origin:'user',sources:input.sourceIds.map(id=>{const source=server.sources.find(item=>item.id===id);return {sourceId:id,alias:'S'+(server.sources.findIndex(item=>item.id===id)+1),title:source?.title??source?.registrableDomain??'',url:source?.url??null,isUserNote:source?.kind==='user_note'};})}));server.sheet=next;return fulfilJson(route,200,next,{ETag:next.etag});
 });
 await page.route(url=>url.pathname===API+'/fact-sheet/generate',async route=>{server.requests.push(route.request());server.generating=true;return fulfilJson(route,202,{runId:'research-run',factSheetId:null,status:'queued'});});
 await page.route(url=>url.pathname===API+'/fact-sheet/approve',async route=>{
  const request=route.request();server.requests.push(request);if(request.headers()['if-match']!==server.sheet?.etag)return fulfilJson(route,412,problem(412,'fact_sheet_conflict'),{'Content-Type':'application/problem+json'});
  const cited=new Set(server.sheet?.facts.flatMap(fact=>fact.sources.map(source=>source.sourceId)));if(server.sources.filter(source=>source.kind==='url'&&source.status==='fetched'&&cited.has(source.id)).length<2)return fulfilJson(route,409,problem(409,'story_sources_required'),{'Content-Type':'application/problem+json'});
  if(server.sheet)server.sheet={...server.sheet,status:'approved',approvedAt:'2026-10-14T00:03:00Z'};return fulfilJson(route,200,server.sheet,server.sheet?{ETag:server.sheet.etag}:{});
 });
 return server;
}
