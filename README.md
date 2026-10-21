# Night Tales Studio

Night Tales Studio turns a story idea into a narrated video and publishes it: story → images → voice → subtitles → MP4 → YouTube and TikTok. It is built for one owner running their own channels, with an autopilot that plans, writes, renders and queues episodes. Vietnamese and English are supported throughout.

**Status:** under active development. It is not production-ready, and there is no hosted service. Progress is tracked in [`STATUS.md`](STATUS.md).

## Architecture

```
Angular SPA ──REST + SignalR──► NightTales.Api ──► PostgreSQL 17 ◄── NightTales.Worker (Hangfire + outbox dispatcher)
                                                                         │
                     OpenAI (story, images) ◄────────────────────────────┤
                     GPU node: MPT + VieNeu (TTS) + faster-whisper ◄──────┘ over HTTP
```

- **Backend:** C# / .NET 10, Clean Architecture + CQRS (`Domain ← Application ← Infrastructure, Api, Worker`, enforced by architecture tests), Mediator, FluentValidation, ErrorOr, EF Core on PostgreSQL 17, Hangfire. The Api never enqueues jobs directly; it writes an outbox that the Worker dispatches.
- **Web:** Angular 22, feature-sliced Clean Architecture, a generated API client from `openapi.json`.
- **Worker node:** local text-to-speech (VieNeu), subtitle alignment (faster-whisper) and video rendering, reached over HTTP. Scripts and pins live in [`worker-node/`](worker-node/README.md).

Diagrams: [`DIAGRAMS.md`](DIAGRAMS.md) and `docs/diagrams/`.

## Repository layout

| Path | Contents |
|---|---|
| `src/` | .NET solution: Domain, Application, Infrastructure, Api, Worker, Fetcher, render-node adapter and server |
| `tests/` | Unit, architecture and integration tests (xunit.v3, Shouldly, ArchUnitNET) |
| `web/` | Angular app |
| `worker-node/` | Bootstrap, start and smoke scripts for the TTS/render node |
| `prompts/` | Versioned prompts, schemas and fixtures |
| `plans/` | Contracts and feature plans (`plans/00-overview.md` is the contract) |
| `tools/` | Developer tools, including `tools/dev/up.sh` for the local stack |
| `design/` | UI design deck and mockups |

## Getting started

Daily development runs natively on macOS without Docker: Homebrew `postgresql@17`, `seaweedfs` and `mailpit`, `dotnet run` for the Api and Worker, and `ng serve` for the web app. `tools/dev/up.sh` starts the whole stack on `127.0.0.1`. The full setup, including the required environment values, is in [`docs/dev-setup.md`](docs/dev-setup.md).

```bash
git config core.hooksPath .githooks          # enables the gitleaks pre-commit hook

dotnet restore NightTales.ci.slnf --locked-mode
dotnet build   NightTales.ci.slnf -c Release --no-restore
dotnet test    NightTales.ci.slnf -c Release --no-build --filter "Category!=Integration&Category!=Node" --minimum-expected-tests 1
```

Integration tests need a PostgreSQL 17 server in `NTS_TEST_PG`; see `docs/dev-setup.md`. API keys are read from the macOS Keychain and are never committed.

## Contributing

Issues and pull requests are welcome. Read [`WORKFLOW.md`](WORKFLOW.md) for the engineering workflow and Definition of Done, and keep changes consistent with `plans/00-overview.md`. CI runs `.github/workflows/backend.yml` and `web.yml`.

## License

Licensed under the [Apache License, Version 2.0](LICENSE). See [`NOTICE`](NOTICE) for third-party material in this repository.
