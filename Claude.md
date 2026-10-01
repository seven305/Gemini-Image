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
IProgress<JobUpdate>, ct)` — Channels worker pool (concurrency = workers; each worker takes the next unused
IProgress<JobUpdate>, ct)` — Channels worker pool (concurrency = workers; each worker takes the next unused
CSV account, generates images with it until Gemini reports its daily limit, closes that browser, takes the next),
Polly retry, account quarantine after threshold, manifest resume, inter-prompt jitter.

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
CAPTCHA/unknown challenge is skipped; sign-ins run in parallel (one per worker), capped by `Gemini:MaxConcurrentSignIns` (0 = no cap). Expect periodic
re-auth; no code removes it fully.

## Verified specifics (from Phase 0 spike)
See `SPIKE_FINDINGS.md` — confirmed selectors, the image-generation model/mode name, login
persistence and download behavior. Source of truth for `GeminiSelectors`.

## Phase status
- Phase 0 (spike): DONE.  Phase 1 (skeleton + contracts + fakes): DONE.
- Phase 2 (real Playwright Gemini session): code complete, awaiting the first live-account run.
  Built: `GeminiSelectors`, `PlaywrightGeminiSession(+Factory)`, `PlaywrightBrowserLauncher`,
  `GoogleSignInDriver` + `GoogleSignInGate` (automated sign-in from CSV credentials incl. TOTP via
  `TotpGenerator`; once per session, parallel up to `Gemini:MaxConcurrentSignIns`), `AccountUnavailableException`,
  `Batch:UseFakeSession` switch, failure diagnostics (screenshot + ARIA snapshot). Google's post-sign-in nags
  (`gds.google.com/web/*` e.g. recovery options, `myaccount.google.com/verification/selfie/*`) are skipped by reopening
  `/app` (`GeminiSelectors.IsPostSignInPrompt`), a bounded number of times.
  Verified so far: DI both ways, Chrome launch, signed-out detection, 25 unit tests.
- Phase 3 (concurrency & resilience): code complete, awaiting the 5-account live milestone run.
  `BatchProcessor`: unbounded queue filled up front; an `AccountUnavailableException` is terminal for the
  account (never retried) and its job is requeued to a healthy worker; the job that trips
  `AccountFailureThreshold` is requeued too; `SessionLost` (browser crashed/closed) relaunches the session up to
  `MaxSessionRestarts` before quarantining; duplicate `ManifestKey`s in a batch are skipped; the manifest is
  re-checked at pickup and before save; the commit (strip + manifest) is non-cancellable once the file is saved;
  `StartupStaggerMs` staggers launches; accounts sharing an email/profile dir are used once. Account rotation:
  `StartupStaggerMs` staggers launches; accounts sharing an email/profile dir are used once. One job (image) per
  prompt — no randomization, no per-account cap. Account rotation: workers draw accounts from the roster in CSV order,
  one turn per account per run; a turn lasts until the account's daily limit (or quarantine / the queue drains); jobs
  left when every account has had its turn fail with `StopReason.AccountsExhausted` (resume on a later run).
  `BatchResult` carries `StopReason`, `AccountsUsed` + `QuarantinedAccounts`. Opt-in `Gemini:ProxyCheckUrl` logs each account's egress IP. Fake knobs
  `Fake:UnavailableAccountIds` / `SessionLostAccountIds` / `AccountLossAfterCalls` reproduce the failure paths offline.
  Daily limit: `AccountUnavailableReason.DailyLimitReached` (thrown when a turn ends without an image and the reply
  matches `GeminiSelectors.IsDailyLimitMessage` — patterns NOT spike-verified, refine from diagnostics after the first
  live hit) ends the account's turn without quarantine and requeues its job; `BatchResult.LimitReachedAccounts`.
  `Fake:DailyLimitAfterImages` (0 = never; appsettings sets 5) makes fake sessions hit the limit.
- Phase 4 (operator UI): code complete. Prompts come from the text box or **Load prompts CSV…** (client layout
  `Image Prompt` + `Section` + `#` (id -> `DesiredFileName`, `A2` -> `A-2`, `C-SPECIAL-1` as-is), other columns ignored; or legacy `Filename | Prompt`; delimiter picked from the header line,
  filenames -> `DesiredFileName`, Section -> `PromptJob.Section`; opened FileShare.ReadWrite so Excel can hold it).
  Each image is saved in `<output>\<Section>\` (`IImageStorage.SaveAsync(..., subfolder, ...)`, sanitized to one folder
  name); a Section prefixes the `ManifestKey` (`A/<key>`). The output folder is runtime state (`OutputLocation`
  singleton, default `Batch:OutputFolder`), picked with **Browse…** and remembered in
  `%LOCALAPPDATA%\GeminiBatch\ui-settings.json`; the manifest is always `<output>\manifest.json` (no `ManifestPath`). The grid
  shows one Pending row per prompt as soon as prompts are loaded/edited (MainForm `BuildJobs`; fresh jobs again on Start;
  loading an account CSV keeps the last run's rows) — Filename (desired name, then
  the saved path relative to the output folder, so the Section folder shows; the job's prompt is the cell tooltip), Status,
  Account, Attempts, Started, Error; no Prompt/Section columns. Concurrency is capped at the enabled-account count. Live grid (`BufferedDataGridView`, double-buffered,
  per-row update, colored Status cell, Error column), StatusStrip counts + progress bar, "Resume detected" note
  (manifest skips = `Skipped` with no error), **Open output folder**, and a non-modal last-run summary box
  (quarantined accounts + reasons). No modal after Start; validation messages only on the Start click.
- Next: Phase 5 deploy (EXIF strip processor, packaging).

## Build / run
`dotnet build` (all projects green), `dotnet test` (Application + Infrastructure suites).
Browser: `Gemini:Channel` defaults to the installed **chrome** — bundled Chromium could not be spawned
on the dev box (see SPIKE_FINDINGS.md), so `playwright install chromium` is only needed if you set
`Channel` to null/empty.
Run GeminiBatch.WinForms: paste prompts and **Start**. Start asks for the account CSV the first time
(`email,password,recovery email,2FA key,proxy`; only the email is required; proxy is a URL such as
`http://user:pass@host:port` or `socks5://host:port`; header optional; `*.csv` is git-ignored) and re-reads it
on every run; **Accounts CSV…** switches files. The CSV is the only account source. Each worker signs its account
in automatically if the profile is signed out — sign-ins run in parallel, capped app-wide by `Gemini:MaxConcurrentSignIns` (`GoogleSignInGate`, 0 = no cap). A minimized Chrome window is restored automatically (`BrowserWindowGuard`). An
account whose sign-in fails (CAPTCHA, unknown challenge, wrong password, no password) is skipped, the batch
continues, and skipped accounts + reasons are listed at the end.
Offline testing: `Batch:UseFakeSession=true` (appsettings.json or `GEMINIBATCH_Batch__UseFakeSession=true`)
swaps the real session for the fake — no browser, no accounts.
Download path: `Gemini:DownloadMode=Net` (default, crash-free CDN capture) or `Ui` (bit-exact original,
but crashed the browser ~60% of the time in the spike). Failures write a screenshot + ARIA snapshot to
`Gemini:DiagnosticsFolder`.