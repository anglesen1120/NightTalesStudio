/**
 * Domain types of the find-sources suggestions block (10.5.8-T1, flag `source_finder`). A candidate is untrusted
 * third-party text: `url`, `title` and `siteName` come from a web search, so they are rendered by interpolation only
 * (never `innerHTML`, never `bypassSecurityTrust*`), linked only as an external `http(s)` link, and never logged or
 * put in telemetry (02.7 `plan.md` §8).
 */

/** The wire `status` of a candidate (02.7.2-T2:39-53); only these three are recognised. */
export type CandidateStatus = 'proposed' | 'added' | 'dismissed';

/** One suggested source of the episode, as `GET …/sources/candidates` answers it. */
export interface SourceCandidate {
  readonly id: string;
  /** Untrusted absolute http(s) URL of the suggestion. Interpolation and `href` only. */
  readonly url: string;
  /** Untrusted result title; `null` when the search carried none (the row then shows the URL). */
  readonly title: string | null;
  /** Untrusted registrable site name the row labels the suggestion with. */
  readonly siteName: string;
  /** The server's own order/quality counter (`1` = best); the list is never re-sorted locally. `0` = unreadable. */
  readonly rank: number;
  readonly status: CandidateStatus;
  /** The episode source the add created; `null` while the candidate is not in the list. */
  readonly sourceId: string | null;
  readonly createdAt: string;
}

/**
 * The server's own cap on the optional query of `POST …/sources/find` (02.7 `plan.md` §4 U1: ≤ 300 chars). The panel's
 * counter enforces it for typing; the store only uses it to decide whether a value may be sent at all, so the client
 * never asks the API for something its validator would refuse (UX only — the server stays the authority).
 */
export const FIND_QUERY_MAX_CHARS = 300;
