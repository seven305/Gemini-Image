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
`IImageProcessor`, `ICsvAccountRoster`, `IPromptSource`, `IJobManifest`. Models: `JobUpdate`,
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
- WinForms UI is authored via the designer (`*.Designer.cs` + `*.resx`), never built programmatically;
  events wired to named handlers. Forms keep a parameterless ctor for the designer plus a `: this()` DI
  ctor. Code-behind (`*.cs`) holds behavior only.

## Anti-detection (Gemini web automation is the dominant risk)
Headful, one account per persistent context/profile, per-account proxy support, human-like
pacing/jitter, low concurrency (default 5). Login is automated from the CSV roster (email, password,
recovery email, TOTP key -> `GoogleSignInDriver`) with no manual fallback in the app: an account that hits a
CAPTCHA/unknown challenge is skipped; sign-ins run one account at a time. Expect periodic
re-auth; no code removes it fully.

## Verified specifics (from Phase 0 spike)
See `SPIKE_FINDINGS.md` — confirmed selectors, the image-generation model/mode name, login
persistence and download behavior. Source of truth for `GeminiSelectors`.

## Phase status
- Phase 0 (spike): DONE.  Phase 1 (skeleton + contracts + fakes): DONE.
- Phase 2 (real Playwright Gemini session): code complete, awaiting the first live-account run.
  Built: `GeminiSelectors`, `PlaywrightGeminiSession(+Factory)`, `PlaywrightBrowserLauncher`,
  `GoogleSignInDriver` + `GoogleSignInGate` (automated sign-in from CSV credentials incl. TOTP via
  `TotpGenerator`; once per session, one account at a time), `AccountUnavailableException`,
  `Batch:UseFakeSession` switch, failure diagnostics (screenshot + ARIA snapshot).
  Verified so far: DI both ways, Chrome launch, signed-out detection, 25 unit tests.
- Phase 3 (concurrency & resilience): code complete, awaiting the 5-account live milestone run.
  `BatchProcessor`: unbounded queue filled up front; an `AccountUnavailableException` is terminal for the
  account (never retried) and its job is requeued to a healthy worker; the job that trips
  `AccountFailureThreshold` is requeued too; `SessionLost` (browser crashed/closed) relaunches the session up to
  `MaxSessionRestarts` before quarantining; duplicate `ManifestKey`s in a batch are skipped; the manifest is
  re-checked at pickup and before save; the commit (strip + manifest) is non-cancellable once the file is saved;
  `StartupStaggerMs` staggers launches; accounts sharing an email/profile dir get one worker. `BatchResult` carries
  `StopReason` + `QuarantinedAccounts`. Opt-in `Gemini:ProxyCheckUrl` logs each account's egress IP. Fake knobs
  `Fake:UnavailableAccountIds` / `SessionLostAccountIds` / `AccountLossAfterCalls` reproduce the failure paths offline.
- Next: Phase 4 UI polish, Phase 5 deploy. Bonus: CSV, naming, EXIF.

## Build / run
`dotnet build` (all projects green), `dotnet test` (Application + Infrastructure suites).
Browser: `Gemini:Channel` defaults to the installed **chrome** — bundled Chromium could not be spawned
on the dev box (see SPIKE_FINDINGS.md), so `playwright install chromium` is only needed if you set
`Channel` to null/empty.
Run GeminiBatch.WinForms: paste prompts and **Start**. Start asks for the account CSV the first time
(`email,password,recovery email,2FA key,proxy`; only the email is required; proxy is a URL such as
`http://user:pass@host:port` or `socks5://host:port`; header optional; `*.csv` is git-ignored) and re-reads it
on every run; **Load CSV…** switches files. The CSV is the only account source. Each worker signs its account
in automatically if the profile is signed out — one sign-in at a time app-wide (`GoogleSignInGate`). An
account whose sign-in fails (CAPTCHA, unknown challenge, wrong password, no password) is skipped, the batch
continues, and skipped accounts + reasons are listed at the end.
Offline testing: `Batch:UseFakeSession=true` (appsettings.json or `GEMINIBATCH_Batch__UseFakeSession=true`)
swaps the real session for the fake — no browser, no accounts.
Download path: `Gemini:DownloadMode=Net` (default, crash-free CDN capture) or `Ui` (bit-exact original,
but crashed the browser ~60% of the time in the spike). Failures write a screenshot + ARIA snapshot to
`Gemini:DiagnosticsFolder`.