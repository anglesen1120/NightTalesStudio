## Prompt provenance (required when this PR touches `prompts/`)

- [ ] No prompt text was copied or translated from InkOS (AGPL-3.0) or any other project.
- [ ] Any idea taken from elsewhere is recorded as one line in `prompts/REFERENCES.md`.
- [ ] `prompts/manifest.json` sha256 entries match the files changed in this PR.

## Migration (required when this PR adds or changes a migration)

- [ ] **Expand-only:** nothing that a running release still writes is dropped or narrowed. `DropTable`,
      `DropColumn` and a narrowing `AlterColumn` are the contract step of NFR-09-6 and need
      `[ContractMigration("why the previous release is over")]` on the migration class — without it the CI
      `migrations` job fails.
- [ ] **Locks:** no `ALTER TABLE … SET NOT NULL` (or a type rewrite) on a table that already has rows without a
      default, and no other statement that holds a lock for the length of a large backfill.
- [ ] **Backfill:** existing rows are filled in batches by a job, never by one blocking statement inside the
      migration.
- [ ] **Down script:** present and sane (`dotnet ef migrations script <prev> <this>`); a destructive `Down` is
      marked in the description.
- [ ] **Model in sync:** `dotnet ef migrations has-pending-model-changes` reports nothing, and the CI
      `migrations` job is green.
- [ ] **Seed dataset:** a migration that adds a table also extends
      `tests/NightTales.Api.IntegrationTests/Migrations/SeedPrevious.cs`, so the next migration is exercised
      over data.

## Research allow-list (required when this PR changes `config/source-finder/allowed-sites.v1.json`)

- [ ] allow-list change: clause quoted
