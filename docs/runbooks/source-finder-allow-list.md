# Source finder: allow-list runbook

Owner: the product owner. Only the owner approves sites. Every change to a site's `status` is a pull request that the owner reviews.

Related: feature plan `plans/features/02-story-writing/02.7-source-finder/plan.md` §3 and §8; task `02.7.2-T1`; DECISIONS §10.10.

## 1. What the file is

`config/source-finder/allowed-sites.v1.json` lists the research sites the source finder may search. It is tracked in git, embedded in the Worker image as a resource (no runtime disk reads), and validated at start-up. If validation fails, the host does not start.

## 2. Schema

```json
{
  "version": 1,
  "sites": [
    {
      "domain": "example.org",
      "name": "Example Archive",
      "language": "en",
      "category": "archive",
      "terms_url": "https://example.org/terms",
      "robots_checked": true,
      "automated_access_basis": "\"Single pages may be retrieved automatically for personal research\" — https://example.org/terms (section 4.2)",
      "status": "approved",
      "reviewed_at": "2026-10-16",
      "reviewer": "Owner name"
    }
  ]
}
```

| Field | Type | Rule |
|---|---|---|
| `version` | integer | Must be `1` (AL1). |
| `domain` | string | Host only: no scheme, path, port or trailing dot; lowercase; punycode (AL2). |
| `name` | string | Display name. |
| `language` | enum | `vi` \| `en` \| `any`. |
| `category` | enum | `encyclopedia` \| `news` \| `court` \| `archive` \| `government` \| `culture`. |
| `terms_url` | string | URL of the terms the basis quotes. |
| `robots_checked` | boolean | See §6. |
| `automated_access_basis` | string or null | Quoted clause + URL, or "official API/bulk-data permission" + URL. Required when `approved` (AL4). |
| `status` | enum | `candidate` \| `approved` \| `rejected`. |
| `reviewed_at` | ISO date or null | Required when `approved` (AL4). |
| `reviewer` | string or null | Required when `approved` (AL4). |

All entries shipped in the first PR are `candidate`. No entry is approved by code, by the plan or by an assistant.

## 3. Start-up rules

The host refuses to start when any rule fails. The failure names the rule id.

| Rule | Check | Failure |
|---|---|---|
| AL1 | JSON parses and `version = 1`. | Host does not start. |
| AL2 | `domain` has no scheme, path, port or trailing dot, and is lowercase punycode. | Host does not start. |
| AL3 | No entry's host or registrable domain matches the Domain blocklist (D11). Example: `m.youtube.com` is refused. | Host does not start. |
| AL4 | Every `approved` entry has non-empty `automated_access_basis`, `reviewed_at` and `reviewer`. | Host does not start. |
| AL5 | At most 100 `approved` entries (W2). | Host does not start. |
| AL6 | No duplicate `domain` values. | Host does not start. |

Startup failure is the safety net. It does not replace review: a PR that passes AL1–AL6 can still be wrong about the terms.

## 4. How a host is matched

`IResearchAllowList.IsAllowed(host)` returns true only when the host equals an `approved` domain, or is a subdomain of one at a label boundary. Examples:

- `www.vnexpress.net` matches an approved `vnexpress.net`.
- `vnexpress.net.evil.com` does not match `vnexpress.net`.
- `notvnexpress.net` does not match `vnexpress.net`.

`Approved(lang)` returns the `approved` entries whose `language` equals `lang` or is `any`. `candidate` and `rejected` entries are never used for search.

The blocklist (D11) wins at every level: AL3 at start-up, and D11 again at extraction and at fetch. An approved entry does not unblock a blocked host.

A redirect to a new host is not followed into the allow-list. The new host needs its own `approved` entry (see §8).

## 5. Reading the start-up log

At start-up the host logs the sha256 of the file it loaded (`IResearchAllowList.Hash`). Compare this hash with the hash of the committed file to confirm which list is running after a deploy. Each deploy should show the hash of the file in the release commit. A different hash means the running image does not contain the reviewed file.

## 6. Zero approved entries

If no entry is `approved`, start-up still succeeds and a warning is logged (`ResearchAllowListStartupValidator`). The source finder is then effectively off. The intended behaviour is that `FindSourcesCommand` returns 404 `feature_disabled`; that command belongs to task 02.7.2-T2 and is not in this change, so until 02.7.2-T2 lands the warning is the only signal. This is expected for a new list. Do not treat it as a bug until the owner has approved at least three sites per language.

## 7. Approval lifecycle

`candidate` → `approved` or `rejected`. Only the owner moves a site to `approved`.

To approve a site, the PR must set:

1. `status` = `approved`;
2. `automated_access_basis` = the quoted clause and its URL (the clause must permit, or not forbid, fetching a single page automatically for research);
3. `robots_checked` = `true`, after the robots.txt check in §9;
4. `reviewed_at` = the review date (ISO `YYYY-MM-DD`);
5. `reviewer` = the reviewer's name.

Never fill in a basis, date or reviewer from memory. If the clause cannot be quoted from the live page, the site stays `candidate`.

To reject a site, set `status` = `rejected` and quote the clause that forbids automated access in `automated_access_basis`. Keep the entry as history; do not delete it.

## 8. Procedures

**Site changes its terms.** The owner sets `status` = `rejected` in a PR with the new quoted clause. After that PR is deployed, the finder stops using the site. Existing entries stay as history.

**Domain moves** (the site redirects to a new host). Only the listed host is allowed. The new host needs its own entry, reviewed under §7. Do not add the new host to the old entry.

## 9. Meaning of `robots_checked`

`robots_checked = true` means the site's `robots.txt` was read and does not disallow `NightTalesStudioBot` or `*` for article paths. Check the rules for the paths the finder will fetch (for example `/wiki/`, `/news/`, `/article/`), not only for `/`. A site with a disallow on article paths is `rejected` or stays `candidate`.

## 10. Basis for approval (DECISIONS §10.10)

DECISIONS §10.10 allows "allow-listed sites whose terms permit automated access". The source finder searches through an external search index. The later page fetch is our own request. The basis for approving a site must therefore cover fetching a single article page on a user's request, with robots respected. A clause that only covers crawling, bulk download or indexing is not enough for this purpose. A clause that covers an official API or bulk-data permission is accepted, with its URL.

## 11. Example approved entry

```json
{
  "domain": "example.org",
  "name": "Example Archive",
  "language": "en",
  "category": "archive",
  "terms_url": "https://example.org/terms",
  "robots_checked": true,
  "automated_access_basis": "\"Single pages may be retrieved automatically for personal research\" — https://example.org/terms (section 4.2), checked 2026-10-16",
  "status": "approved",
  "reviewed_at": "2026-10-16",
  "reviewer": "Owner name"
}
```

The domain, clause and URL in this example are placeholders. Do not copy them into the committed file.

## 12. Reviewer checklist

- [ ] `terms_url` opened today; the quoted clause appears on the live page and the URL is correct.
- [ ] The clause permits, or does not forbid, fetching a single article page automatically for research (§10).
- [ ] `robots.txt` read; `NightTalesStudioBot` and `*` are not disallowed for article paths (§9); `robots_checked` matches.
- [ ] `category` and `language` are correct.
- [ ] `domain` passes AL2 and is not on the blocklist (AL3); it is not a subdomain already covered by another entry that should be used instead.
- [ ] If approving: `reviewed_at` is today's date and `reviewer` is the owner's name (AL4).
- [ ] Approved count after this PR is at most 100 (AL5); no duplicate domain (AL6).
- [ ] The PR is a status change reviewed by the owner, and the checklist line `allow-list change: clause quoted` is ticked.
- [ ] Start-up hash noted for the release (§5).
