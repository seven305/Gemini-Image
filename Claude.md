# Gemini Batch Image Generator — Project Context

## What this is
Internal Windows desktop tool that automates our manual Gemini web-chat image workflow:
provide a batch of prompts, run multiple Gemini sessions concurrently across ~10 paid
Google accounts, auto-download each image, continue until the batch is done. Runs on an
RDP box. Priority: simple, reliable, cheap to build/maintain. ~30 images/project, up to
~200/day.

## Stack (fixed)
.NET 10, C# latest (nullable + implicit usings on). WinForms shell (net10.0-windows).
Playwright for .NET (Chromium, headful, persistent contexts). Serilog. Polly.Core 8.
CsvHelper, Magick.NET-Q8-AnyCPU, System.Security.Cryptography.ProtectedData (DPAPI).
Microsoft.Extensions.Hosting/DI/Options.

## Architecture — Clean Architecture, dependency rule points inward
- `GeminiBatch.Domain` — entities, enums, value objects. ZERO deps.
- `GeminiBatch.Application` — ports (interfaces), BatchOptions, BatchProcessor. -> Domain only.
- `GeminiBatch.Infrastructure` — Playwright/Serilog/Magick/CsvHelper/DPAPI impls. -> Application.
- `GeminiBatch.WinForms` — UI + composition root (DI). -> Application + Infrastructure.
No Infrastructure type may be referenced from Application. Interfaces live in Application.

## Key contracts (already built in Phase 1)
Domain: `PromptJob` (Id, Prompt, DesiredFileName?, ManifestKey [CSV name else FNV-1a hash],
Status, Attempts, AssignedAccountId, SavedPath, LastError), `GeminiAccount` (Id, Email,
UserDataDir, Proxy?, Enabled, quarantine state), `ProxySettings(Server,User?,Pass?)`,
`JobStatus` enum.
Application ports: `IGeminiSession`(+`IGeminiSessionFactory`), `IImageStorage`,
`IImageProcessor`, `IAccountStore`, `IPromptSource`, `IJobManifest`. Models: `JobUpdate`,
`BatchResult`, `BatchOptions`. `BatchProcessor.RunAsync(jobs, accounts, concurrency,
IProgress<JobUpdate>, ct)` — Channels worker pool (one account per worker), Polly retry,
account quarantine after threshold, manifest resume, inter-prompt jitter.

## Conventions (enforce)
- Async all the way; never block the UI thread. Dispose sessions/contexts.
- ALL Gemini-specific DOM knowledge (selectors, model picker) lives ONLY in `GeminiSelectors`
  — the single highest-churn surface. Nothing else touches the page.
- Never overwrite files: collisions become name_2, name_3 (atomic FileMode.CreateNew reserve,
  concurrency-safe).
- Batch must survive individual job/account failures; crash-resume via manifest.
- WinForms: `Progress<JobUpdate>` created on UI thread; update single row by Id via
  Dictionary<Guid, DataGridViewRow> (no rebind). Stop -> CancellationTokenSource.

## Anti-detection (Gemini web automation is the dominant risk)
Headful, one account per persistent context/profile, per-account proxy support, human-like
pacing/jitter, low concurrency (default 5). First-run login is manual (2FA). Expect periodic
re-auth; no code removes it fully.

## Verified specifics (from Phase 0 spike)
See `SPIKE_FINDINGS.md` — confirmed selectors, the image-generation model/mode name, login
persistence and download behavior. Source of truth for `GeminiSelectors`.

## Phase status
- Phase 0 (spike): DONE.  Phase 1 (skeleton + contracts + fakes): DONE.
- Phase 2 (real Playwright Gemini session): code complete, awaiting the first live-account run.
  Built: `GeminiSelectors`, `PlaywrightGeminiSession(+Factory)`, `PlaywrightBrowserLauncher`,
  `PlaywrightAccountLoginService` (manual sign-in + 2FA), `AccountUnavailableException`,
  `Batch:UseFakeSession` switch, failure diagnostics (screenshot + ARIA snapshot).
  Verified so far: DI both ways, Chrome launch, signed-out detection, 25 unit tests.
- Next: Phase 3 concurrency hardening, Phase 4 UI polish, Phase 5 deploy. Bonus: CSV, naming, EXIF.

## Build / run
`dotnet build` (all projects green), `dotnet test` (Application + Infrastructure suites).
Browser: `Gemini:Channel` defaults to the installed **chrome** — bundled Chromium could not be spawned
on the dev box (see SPIKE_FINDINGS.md), so `playwright install chromium` is only needed if you set
`Channel` to null/empty.
Run GeminiBatch.WinForms: pick an account, **Login…** (sign in manually once per profile; the window
closes itself when the session is detected), then paste prompts and **Start**.
Offline testing: `Batch:UseFakeSession=true` (appsettings.json or `GEMINIBATCH_Batch__UseFakeSession=true`)
swaps the real session for the fake — no browser, no accounts.
Download path: `Gemini:DownloadMode=Net` (default, crash-free CDN capture) or `Ui` (bit-exact original,
but crashed the browser ~60% of the time in the spike). Failures write a screenshot + ARIA snapshot to
`Gemini:DiagnosticsFolder`.