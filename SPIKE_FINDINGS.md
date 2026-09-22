# Spike findings — Gemini web automation (Phase 0)

Date: 2026-09-21 · Stack: .NET 10 console, Microsoft.Playwright 1.62.0 (bundled Chromium 151), headful,
`LaunchPersistentContextAsync("./profile")`. All code in `Program.cs` (modes: `login`, `inspect`, `generate`).
Raw evidence in `./out` (ARIA snapshots `aria-*.yml`, DOM dumps `dom-*.html`, screenshots, `run*.log`).

## TL;DR

| Question | Answer |
|---|---|
| Can one account log in once and reuse the session? | **Yes.** Login persisted across process relaunches (22 Google cookies incl. `SID`, `__Secure-1PSID`, `__Secure-3PSID`). No CAPTCHA / "unusual activity" / "browser may not be secure" seen — but see *browser binary* below. |
| Can we select image mode and submit a prompt? | **Yes**, reliably, via role/name locators (13/13 runs). |
| Can we detect generation complete? | **Yes**, `<generated-image> img.loaded` + "Stop response" button gone. 15–26 s per image. |
| Can we download via the UI download control? | **Unreliable.** Playwright's download interception works (1408×768 JPEG, ~940 KB) but **crashes the Chromium browser process ~60% of the time** (3 OK / 5 crash on Chrome 153, 1 OK / 2 crash on Edge 153). |
| Is there a crash-free way to get the file? | **Yes** — capture the CDN response Gemini itself fetches (`lh3.googleusercontent.com/rd-gg-dl/<id>=s1024-rj`), then re-request it with `=s0` through the authenticated context → full 1408×768 JPEG, 100% of runs. |
| End-to-end time | **44.3 s** (run #13, net capture) / **52.8 s** (run #4, UI download). ~21 s of that is Gemini page load; prompt→image 15–26 s; download 3 s. |

## Locators (the fragile surface)

All found from live ARIA snapshots (`page.Locator("body").AriaSnapshotAsync()`), not guessed. Gemini is an Angular app
with custom elements; the accessible names are stable-looking and are what the code uses. Custom element / attribute
alternatives are listed as backups.

| Purpose | Locator used (Playwright .NET) | Backup / DOM detail |
|---|---|---|
| Prompt input | `GetByRole(Textbox, Name = "Enter a prompt for Gemini")` | `rich-textarea div.ql-editor[contenteditable][role=textbox]` (Quill editor). `FillAsync` works. Placeholder changes to "Describe your image" once the image tool is on. |
| Signed-in check | Google cookie `SID`/`__Secure-1PSID` present **and** no `GetByRole(Button, Name="Sign in", Exact)` | Signed-out Gemini still renders the prompt box, so the box alone proves nothing (that bit me once). Account link: `link "Google Account: <name> (<email>)"`. |
| "Keep in mind" first-visit notice | `GetByRole(Button, Name = "Got it")` (click if present) | Appears on fresh profiles; harmless if absent. |
| Tools menu | `GetByRole(Button, Name = "Upload & tools")` — the "+" button left of the prompt | Opens `menu "Menu options"`. |
| **Image mode** | `GetByRole(Menuitemcheckbox, Name = "Create image")` | Once toggled a chip appears: `button "Deselect Images"` (text "Images") and a `button "Aspect ratio"`. Signed-out this item is `[disabled]`. |
| Model picker | `button "Open mode picker, currently Flash"` → menu items `"3.5 Flash-Lite …"`, `"3.6 Flash …"` (selected by default), `"3.1 Pro …"`, `"Extended thinking …"` | **Not needed** for image generation — left at default. Backup: `[data-test-id='bard-mode-menu-button']`. |
| Submit | `GetByRole(Button, Name = "Send message")` | Only exists once the textbox is non-empty. Becomes `button "Stop response"` while streaming. |
| Generated image | `Locator("model-response").Last.Locator("generated-image img.loaded")` | `<model-response> … <generated-image> <single-image> <img class="image animate loaded" alt=", AI generated" src="blob:https://gemini.google.com/…">`. Also exposed as `button ", AI generated"` (opens viewer). |
| Generation complete | image above visible **then** `GetByRole(Button, Name="Stop response")` hidden | Image can appear while "Stop response" is still shown / "Creating your image" text present — wait for both. |
| Download control | `GetByRole(Button, Name = "Download full size image")` | `<download-generated-image-button> gem-icon-button[arialabel="Download full size image"][data-test-id="download-generated-image-button"]`. Siblings: `"Share image"`, `"Copy image"`. Visible without hover in the snapshot; I hover the image first anyway. Download suggested filename: `Gemini_Generated_Image_<16 chars>.jpg`. |

## Model / mode actually used

- UI path: **Tools menu → "Create image"** (a `menuitemcheckbox`); the mode picker stays on the default **"3.6 Flash"**.
  There is no image model in the mode picker itself.
- The UI never names the underlying image model. Page config strings in the DOM mention `"Nano Banana 2"` and
  `"🍌 Nano Banana Pro is now on Gemini 3 Pro"`, so the Flash path is most likely Nano Banana 2 and the Pro mode
  routes to Nano Banana Pro — **inferred, not verified**.
- Output: JPEG **1408×768** (16:9 default), ~920–980 KB, via the UI download. The in-page `<img>` is a 1024×559 preview.
- Account note: the sidebar shows an **"Upgrade"** link, i.e. the test account looks like the free tier, not paid. Paid
  tiers may show different models/limits; nothing in this spike depended on it.

## Login persistence & detection

- **Persisted**: `login` mode now polls until signed in, closes the browser, relaunches with the same `user-data-dir`
  and re-asserts — passed (run log `login PERSISTED across relaunch`). Every later run reused it with no prompt.
- **No CAPTCHA, no 2-step re-verification, no "This browser or app may not be secure"** in ~15 launches over 2 days.
- Launch flags used: `IgnoreDefaultArgs = ["--enable-automation"]`, `--disable-blink-features=AutomationControlled`.
  `navigator.webdriver` was not explicitly checked.
- Gotcha that cost time: `page.PauseAsync()` + Inspector "Resume" is a bad handoff for manual login (easy to resume
  too early / close the wrong window). Polling for the `SID` cookie is much better.

## Browser binary (important environment finding)

- Playwright's **bundled Chromium 151 cannot be spawned on this machine**: `spawn UNKNOWN` (libuv errno -4094) from
  Playwright's own Node, even after copying the binary to `%TEMP%`; no mark-of-the-web stream on the exe. The
  installed **Chrome 153** and **Edge 153** spawn fine. Cause unknown (AV / Smart App Control / policy?). Not Google-related.
- So all runs used `Channel = "chrome"` (or `"msedge"` for the comparison runs). This is a 2-major-version mismatch
  against what Playwright 1.62 was tested with — likely relevant to the crash below.

## The download crash

- Symptom: clicking "Download full size image" kills the **browser process** (Crashpad dump `ptype=browser`,
  timestamps match the run logs) → `TargetClosedException`. The generation itself never failed.
- Reproduced on Chrome 153 (runs 2, 3, 5 crashed; 1, 4 OK) and Edge 153 (run 8 OK; 9, 10 crashed).
  Waiting for the response to finish before clicking made no difference (run 5 crashed after completion).
- Downloads on bundled Chromium could not be tested (spawn failure above).
- Suspects, in order: Playwright 1.62 download interception (`Browser.setDownloadBehavior` over CDP) vs Chromium 153;
  then the Chromium download-bubble path. Not caused by the page itself.

## Download alternatives measured

| Method | Result |
|---|---|
| UI button + Playwright `Download` event | 1408×768 JPEG, ~940 KB, but ~60% browser crash rate on 153 |
| `fetch(img.src)` in page | **fails** — Gemini revokes the `blob:` URL once the `<img>` has loaded |
| Canvas re-encode of the `<img>` | works every time, but only the **1024×559 preview** (PNG ~1.3 MB) |
| Network capture: `page.Response` for `lh3.googleusercontent.com/rd-gg-dl/<id>=s1024-rj?alr=yes` + `BodyAsync()` | works, 146 KB JPEG at 1024×559 (same preview); crash-free |
| Same URL re-requested as `…=s0` via `ctx.APIRequest` (session cookies) | **works: 1408×768 JPEG, 190 KB, 2.6 s, crash-free** (run #13). Same pixel size as the UI download; smaller file because the CDN re-encodes at lower JPEG quality than the "full size" download (940 KB). |

**Recommendation for Phase 1:** use the network-capture path (`GEMINI_DOWNLOAD=net`, now the default) — it is the only
method that was 100% reliable here *and* returns the full 1408×768. Keep the UI download (`GEMINI_DOWNLOAD=ui`) as the
"bit-exact original" path once the Playwright/browser version mismatch is resolved. Note `=s0` is a Google
image-serving convention, so a URL-shape change on Google's side would break it; the fallback is the `=s1024` body
Gemini fetched anyway (preview res).

## Timings (run #4, the clean UI-download run)

| Step | Time |
|---|---|
| Navigate to gemini.google.com/app (cold, cached profile) | 20–24 s **— ~20 s of this is the spike's own `WaitForLoadState(NetworkIdle, 20s)` timing out; Gemini never goes network-idle.** DOMContentLoaded + prompt box visible is ~3–5 s. Real end-to-end is ≈ 25 s. |
| Signed-in assert, dismiss notice, select Create image, fill, submit | ~2 s |
| Prompt → `<img class="loaded">` | 15–26 s (10 runs: 14.7, 14.9, 15.0, 16.0, 17.2, 17.8, 18.4, 18.5, 20.3, 25.9) |
| Download (UI) | 3–10 s |
| **Total** | **52.8 s** as measured (best 40 s with network capture); **≈ 25–30 s once the NetworkIdle wait is dropped** |

## Things likely to break under concurrency / automation detection

1. **One Chromium per `user-data-dir`.** A second `LaunchPersistentContextAsync` on the same profile fails on the profile
   lock. Multiple accounts ⇒ one profile dir each; multiple prompts on one account ⇒ serialize, or use tabs/pages in one
   context (untested).
2. **The download crash** is the #1 reliability issue. Either pin Playwright ↔ browser versions (get bundled Chromium
   spawning, or install the Chrome-for-Testing build Playwright expects) or avoid the UI download entirely (network
   capture). Also, Chrome writes a Crashpad dump into the profile on each crash — the profile keeps working.
3. **Accessible names are localized.** All locators are English strings ("Send message", "Create image",
   "Download full size image"). An account whose Google language isn't English breaks every locator. Backups: the
   custom-element names (`generated-image`, `download-generated-image-button`, `model-response`) and
   `data-test-id="download-generated-image-button"` / `bard-mode-menu-button`.
4. **Gemini UI churn.** Angular `_ngcontent` hashes change every deploy — never use them. Role+name and custom-element
   tags are the only stable handles seen.
5. **Detection surface**: Chrome's default automation banner is suppressed (`--enable-automation` removed), but
   `navigator.webdriver` and CDP were not checked; Google did not react in ~15 launches / 13 generations from one IP.
   Rate limiting per account for image generation is unknown (free tier will have a daily cap).
6. **The "Keep in mind" notice and "Got it"** only appear once per profile; other one-off dialogs (feature tours,
   consent updates) will appear over time and need a generic "dismiss if present" step.
7. **Image appears before the turn ends.** Anything that reads the response too early (e.g. multiple images, text +
   image) must wait for "Stop response" to disappear.
8. **Downloads land as `.jpg` with a random suffix**; there is no prompt/ID in the filename — map by turn, not by name.
9. **Every run creates a saved conversation** in the account's Gemini activity. The header has a `button "Temporary chat"`
   (seen in every signed-in snapshot) — untested, but Phase 1 should use it to avoid polluting history / triggering
   any "unusual activity" heuristics from thousands of chats. Related: `button "Aspect ratio"` appears once the image
   tool is on, so output size is automatable.
10. **Crashpad dumps (~1.6 MB each) accumulate in `profile/Crashpad/reports`** despite `--disable-breakpad`; prune them.
11. **The `net` capture depends on the CDN URL shape** (`rd-gg-dl/<id>=s1024-rj`), which is Google's image-serving
   convention, not a Gemini contract.

## How to run

```
set GEMINI_CHANNEL=chrome
dotnet run -- login      # sign in manually in the window; auto-detects, relaunches, verifies persistence
dotnet run -- inspect    # dumps aria/dom/screenshots of idle, model menu, tools menu, typed prompt to ./out
dotnet run -- generate   # GEMINI_DOWNLOAD=net (default: CDN capture + =s0) | ui (Playwright Download event) | blob (canvas preview)
```
