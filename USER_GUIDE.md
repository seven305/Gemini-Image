# Gemini Batch Image Generator — Operator Guide

This guide is for the people who run batches. It describes the app as it is built today.

> **About the screenshots:** they were taken from the real app running in its offline demo mode, with made-up
> accounts (`operator01@example.com` …) and the three example prompts used in this guide. Because of that:
> saved images are `.png` placeholders, and error texts in the demo shots start with `[fake]`. In a real run
> you will see real images and Google-specific reasons instead. Personal folders in file dialogs are blanked out.

---

## 1. What this app does

- You give it a list of prompts and a list of Google accounts.
- It opens Gemini in Chrome for each account, asks Gemini to make an image, and saves the image to a folder.
- Several accounts work at the same time. Each account signs itself in automatically.
- If something fails, the batch keeps going. If the app is stopped or crashes, starting again picks up where it left off.

![Main window](docs/img/main-window.png)

---

## 2. One-time setup

### 2.1 Install and launch

1. Make sure **Google Chrome** is installed on the machine. The app uses the installed Chrome.
2. Copy the app folder you were given to the machine (for example `C:\GeminiBatch\`).
3. Double-click **`GeminiBatch.WinForms.exe`** in that folder.
4. The window **Gemini Batch Image Generator** opens.

![App folder](docs/img/app-folder.png)

The app creates these folders next to the exe the first time they are needed:

| Folder | What is in it |
|---|---|
| `output\` | Your images, plus `manifest.json` (the resume record) |
| `logs\` | Daily log files |
| `profiles\` | One saved Chrome profile per Google account (keeps accounts signed in) |
| `diagnostics\` | Screenshots taken when a generation fails |

> Do not delete `profiles\` between runs. It is what keeps accounts signed in, so the app does not have to log in again every time.

### 2.2 Prepare the accounts CSV

The accounts CSV is the only place the app gets accounts from. It has up to five columns, **in this order**:

| Column | Required? | What to put |
|---|---|---|
| 1. email | **Yes** | The Google account email |
| 2. password | Recommended | The account password. Without it the app cannot sign the account in. |
| 3. recovery email | Optional | Used if Google asks to confirm the recovery email |
| 4. 2FA key | Optional | The authenticator **secret key** (the text code shown when you set up an authenticator app — not a 6-digit code) |
| 5. proxy | Optional | e.g. `http://user:pass@host:port` or `socks5://host:port` |

Example (`accounts.csv`):

```csv
email,password,recovery email,2FA key,proxy
jane.doe@gmail.com,Pa55word!,jane.backup@gmail.com,JBSWY3DPEHPK3PXP,http://user:pass@10.0.0.5:8080
sam.lee@gmail.com,An0therPass,,,
```

- The header row is optional. Any row whose first column is not an email is ignored.
- Leave a cell empty if you do not have that value.
- If a proxy password contains `@` or `:`, write it URL-encoded (`@` → `%40`, `:` → `%3A`).
- A proxy value that is not a valid proxy URL stops the whole CSV from loading. Fix it and load again.
- The same email listed twice is only used once.
- The file can stay open in Excel while the app reads it.
- Keep this file private. The app keeps the passwords in memory only; it never writes them to logs.

![Accounts CSV example](docs/img/accounts-csv-example.png)

### 2.3 Load the accounts

1. Click **Accounts CSV…**.
2. Pick your accounts CSV in the **Select account CSV** dialog.
3. The label to the right changes to e.g. **5 account(s) · accounts.csv**.

![Accounts dialog](docs/img/accounts-dialog.png)

If you skip this, the app asks for the file the first time you click **Start**. After that it remembers the file and **re-reads it on every Start**, so edits you make to the CSV are picked up automatically. Click **Accounts CSV…** again to switch to a different file.

![Accounts loaded](docs/img/accounts-loaded.png)

### 2.4 How login works (automatic)

You do **not** log in by hand. For each account, when its turn comes:

1. A Chrome window opens using that account's own saved profile.
2. If the profile is still signed in, Gemini opens straight away.
3. If it is signed out, the app signs in using the CSV: email → password → authenticator code (made from the 2FA key) or recovery email confirmation. It also clicks past the account chooser and Google's "after sign-in" prompts (such as "add recovery options" or the "verify with a selfie" page — these are skipped and Gemini is opened).
4. Only **one account signs in at a time** across the whole app. Other accounts wait their turn. This is on purpose — many sign-ins at once from one machine look suspicious to Google.

![Google sign-in page](docs/img/sign-in-browser.png)

*The Google sign-in page the app fills in for you. You do not type anything here.*

The app **gives up** on an account (and moves its image to another account) when:

- Google shows a **CAPTCHA**.
- Google asks for an authenticator code but the CSV has **no 2FA key**.
- Google asks to confirm the recovery email but the CSV has **none**.
- A step is rejected twice (e.g. **wrong password** or a rejected code).
- Google shows a challenge the app does not know (e.g. "tap yes on your phone" with no other option).
- Sign-in does not finish within **3 minutes**.
- The CSV has **no password** and the profile is signed out.

The skipped account and the reason are listed in the summary box at the end of the run. There is no manual fallback in the app — see [Troubleshooting](#6-troubleshooting).

> **Do not click or type in the Chrome windows while a batch is running.** The app is driving them.

### 2.5 Output folder

- Images go to the `output` folder next to the exe by default.
- The full path is shown in the box beside **Open output folder**.
- To change it, click **Browse…** (right of the path) and pick or create a folder. The app remembers it for the next launch.
- Prompts from a CSV with a **Section** column are saved in a sub-folder per section, e.g. `D:\Images\A\`, `D:\Images\B\` (see [3.2](#32-loading-a-prompts-csv)).
- The resume record `manifest.json` always sits in the chosen folder, so each output folder resumes on its own.
- **Browse…** is locked while a batch is running.

### 2.6 Concurrency

**Concurrency:** is how many Chrome windows (accounts) work at the same time.

- It starts at **5** (from `appsettings.json`).
- Once accounts are loaded it can never be higher than the number of accounts. The hint next to it shows e.g. **max 5 (enabled accounts)**.
- Lower is safer. Higher is faster but more likely to trigger Google checks. 3–5 is a good range.
- You cannot change it while a batch is running.

### 2.7 Settings you can change

Settings live in **`appsettings.json`** in the app folder. Open it in Notepad, change a value, save, and **restart the app**. Keep the quotes and commas exactly as they are.

![appsettings.json](docs/img/appsettings.png)

| Setting | Default | What it does |
|---|---|---|
| `Batch:DefaultConcurrency` | `5` | Starting value of **Concurrency:** |
| `Batch:OutputFolder` | `output` | Default output folder (relative = next to the exe), used until you pick one with **Browse…** |
| `Batch:MaxImagesPerAccount` | `1` | Images each account makes before its window closes and the next account takes over. `0` = no limit. |
| `Batch:RandomizePrompts` | `true` | See [3.4](#34-how-many-images-will-be-made). `false` = each prompt once, in order. |
| `Batch:MaxRetriesPerJob` | `3` | Retries for one image on the same account before giving up |
| `Batch:GenerationTimeoutSeconds` | `180` | How long to wait for Gemini to finish one image |
| `Batch:StartupStaggerMs` | `4000` | Delay between opening each Chrome window at the start |
| `Gemini:Channel` | `chrome` | Browser to use (`chrome` or `msedge`) |
| `Gemini:WindowSize` | `1280,900` | Size of each Chrome window |
| `Gemini:DiagnosticsFolder` | `diagnostics` | Where failure screenshots go |
| `Logging:Folder` | `logs` | Where log files go |

Leave every other setting alone unless a developer asks you to change it. In particular, `Batch:UseFakeSession` must stay `false` for real runs (when `true`, no browser opens and no real images are made).

---

## 3. Preparing prompts

You can type prompts or load a prompts CSV. Use the CSV when you want images sorted into section folders (or to choose file names).

### 3.1 Typing prompts

1. Click in the big box under **Prompts (one per line, # for comments) — or load a prompts CSV:**.
2. Type or paste one prompt per line.
3. Lines starting with `#` are ignored. Blank lines are ignored.
4. The label under the box shows e.g. **3 prompt(s) loaded**.

```text
# Product shots
A red ceramic mug on a wooden table, soft morning light
A minimalist desk lamp on a white background
A stack of hardcover books, top-down view
```

Typed prompts get automatic file names (see [5.2](#52-file-names)).

![Prompts typed](docs/img/prompts-typed.png)

### 3.2 Loading a prompts CSV

1. Click **Load prompts CSV…**.
2. Pick the file.
3. The prompts appear in the box, and the label shows e.g. **44 prompt(s) loaded from prompts.csv**.
4. The **Section** column in the grid shows the folder each image goes to.

**Section format** (the client sheet): the prompt is read from the **`Image Prompt`** column and the folder from the **`Section`** column. Every other column (`#`, `Blend Ratio`, …) is ignored.

```text
#,Section,Blend Ratio,Woman Type,Flag Type,Image Prompt
A2,A,Mixed — Cash & Wealth Signals,N,OBJECT,"A contemporary home kitchen, warm afternoon window light. …"
B1,B,Cash Dominant — LP1 Background,N,FABRIC,"An older German man, early 60s, …"
```

Each image is saved in `<output folder>\<Section>\`, e.g. `output\A\`. A row with an empty Section goes straight into the output folder. Files get automatic names in this format.

**Filename format** — pipe (`|`) separated, so commas in prompts are safe:

```text
Filename | Prompt
red_mug | A red ceramic mug on a wooden table, soft morning light
desk_lamp | A minimalist desk lamp on a white background
book_stack | A stack of hardcover books, top-down view
```

Rules:

- The **first line must be a header** with a column named `Image Prompt` (optional `Section`) or `Prompt` (optional `Filename`; a `Section` column works here too). Upper/lower case does not matter.
- The separator is taken from the header line: `|`, tab, `;`, or `,` (in that order of preference).
- With commas as the separator, any prompt that contains a comma must be in double quotes. The pipe format avoids this.
- Write file names **without** an extension (`red_mug`, not `red_mug.jpg`). The app adds the right one.
- Rows with an empty prompt are skipped. An empty filename means an automatic name.
- The CSV may stay open in Excel while you load it.

![Prompts CSV example](docs/img/prompts-csv-example.png)
![Prompts CSV loaded](docs/img/prompts-csv-loaded.png)

> **Careful:** if you type anything in the prompt box after loading a CSV, the app switches back to "typed prompts" and **the file names from the CSV are dropped**. To get them back, load the CSV again.

### 3.3 Check the preview

Before you press **Start**, the grid already lists the images the app plans to make. Each row shows the **Prompt** (hover for the full text), the **Section** (empty = output folder itself), the **Filename** (or **(auto)**), and **Status** = **Pending**. The bottom bar shows **Ready — N image(s) to run**.

### 3.4 How many images will be made

With the default settings (`RandomizePrompts` on, `MaxImagesPerAccount` = 1):

- **Each account makes one image**, from a prompt picked at random from your list. Prompts can repeat.
- So **number of images = number of accounts**, no matter how many prompts you give. 5 prompts and 10 accounts → 10 images.
- The label shows this, e.g. **5 prompt(s) loaded · 10 image(s) planned across the accounts**.
- The random choice is fixed for the same prompts + same accounts, so re-running the same batch gives the same plan (this is what makes resume work).

If `RandomizePrompts` is `false`, each prompt is made once, in order, and accounts take turns. If there are more prompts than accounts × `MaxImagesPerAccount`, the extra prompts fail with **No accounts left to run this job (all used or quarantined).**

---

## 4. Running a batch

### 4.1 Start

1. Check prompts, accounts, and **Concurrency:**.
2. Click **Start**.
3. If no accounts CSV was chosen yet, pick it now.
4. The bottom bar shows **Signing in accounts and running N image(s)…**.
5. Chrome windows open one after another (a few seconds apart). Leave them alone.

If something is missing, a **Cannot start** message explains what to fix (for example, "Enter at least one prompt…"). Nothing pops up after the run has started.

While running, the prompt box, **Concurrency:**, **Load prompts CSV…**, **Accounts CSV…** and **Browse…** are locked.

### 4.2 Reading the grid

![Running grid](docs/img/running-grid.png)

| Column | Meaning |
|---|---|
| **Prompt** | The prompt (first 80 characters; hover for all of it) |
| **Section** | The sub-folder of the output folder the image is saved in (empty = the output folder itself) |
| **Filename** | Planned name, then the real saved file name once done |
| **Status** | Where the image is (coloured, see below) |
| **Account** | The account working on it (the part of the email before `@`) |
| **Attempts** | Tries so far |
| **Error** | Why it failed or was moved (hover for the full text) |

| Status | Colour | Meaning |
|---|---|---|
| **Pending** | white | Waiting for an account |
| **Running** | blue | Gemini is making the image |
| **Downloading** | blue | Saving the image |
| **Retrying** | amber | A try failed; trying again |
| **Completed** | green | Saved |
| **Failed** | red | Gave up — see **Error** |
| **Skipped** | grey | Not made: already done in an earlier run, or a duplicate in this batch (the **Error** column says which) |

A row that goes back to **Pending** with an error message was moved to another account because its account was lost (for example, signed out and could not sign back in). That is normal.

### 4.3 The status bar

![Status strip](docs/img/status-strip.png)

- Live counts, e.g. **3/10 complete · 0 failed · 2 running · 1 skipped**.
- A progress bar on the right.
- **Resume detected: N already done, skipping** (green) when earlier results were found.

### 4.4 Stopping safely

1. Click **Stop**.
2. The bar shows **Stopping… (finishing saves in progress)**.
3. Wait. Any image that is already being saved is finished properly. Images still being generated go back to **Pending**.
4. The bar then shows e.g. **Stopped — 4 completed · 0 skipped · 0 failed · 6 not processed**.

![Stopped](docs/img/stopped.png)

Nothing is lost. Click **Start** again later to continue — finished images are skipped.

Closing the window also stops the batch the same way.

### 4.5 When the run ends

The bar shows **Done — …** and the box at the bottom shows the summary:

- Counts: completed, skipped, failed, and how many accounts were used.
- **Resume: N image(s) were already done in an earlier run and were skipped.** (if any)
- **Stopped early: every account had its turn.** — there were more images than working accounts.
- **Stopped early: all accounts quarantined.** — no account could work.
- **Skipped / quarantined accounts:** each problem account with its reason.

![Run summary](docs/img/run-summary.png)

An account is "quarantined" (not used again this run) when it cannot sign in, when its browser crashes more than once, or after 3 failed images in a row. The quarantine is cleared automatically on the next **Start**.

---

## 5. Results

### 5.1 Where images go

Click **Open output folder**. By default it is the `output` folder next to the exe; change it with **Browse…**. Images from a Section CSV are inside one sub-folder per section (`A\`, `B\`, …).

![Output folder](docs/img/output-folder.png)

Images are saved in the format Gemini serves — usually **`.jpg`** (sometimes `.png` or `.webp`). They are Gemini's full-size image as served by Google's image server.

### 5.2 File names

- From a prompts CSV with a filename: `red_mug.jpg`.
- Typed prompts (or an empty filename): `gemini_YYYYMMDD_HHMMSS.jpg`, e.g. `gemini_20260930_142501.jpg`.
- **Files are never overwritten.** If the name is taken in that folder, the app adds a number: `red_mug.jpg`, `red_mug_2.jpg`, `red_mug_3.jpg`, … This is also what happens when the same prompt is used more than once in a randomized batch.
- Characters Windows does not allow in file names are replaced with `_`.

### 5.3 Re-running and resume

The file `manifest.json` in the output folder records every image that was saved.

- When you click **Start**, the app checks this record. Any planned image already in it is marked **Skipped** (with no error) and not made again. The bar shows **Resume detected**.
- This is how a stopped or crashed batch continues: **use the same prompts and the same accounts CSV and click Start.**
- Images that **Failed** are not in the record, so **Start** tries them again.

Things to know:

- **Changing the prompts or the list of emails in the accounts CSV changes the random plan.** The app then sees new images to make and makes them (saved as new files; nothing is overwritten). Change passwords/proxies freely — only the emails matter for the plan.
- **Deleting an image file does not make the app regenerate it** — it is still in `manifest.json`.
- **To start completely fresh**, move or rename `manifest.json` (or use a new output folder). Old images stay; new ones get `_2`, `_3` names if the names clash.

![Resume detected](docs/img/resume-detected.png)

---

## 6. Troubleshooting

### An account was skipped / quarantined

Read the reason in the summary box (and the **Error** column).

| Reason mentions | What to do |
|---|---|
| **CAPTCHA** | Google wants a human check. Sign in to that account yourself in normal Chrome (ideally through the same proxy), complete the check, then run again. Rest the account for a while if it keeps happening. |
| **no password** | Add the password to column 2 of the accounts CSV. |
| **no 2FA key** | Add the authenticator secret key to column 4. |
| **recovery email … has none** | Add the recovery email to column 3. |
| **password step was rejected** | The password is wrong or changed. Fix column 2. |
| **2FA code step was rejected** | The 2FA key is wrong, or the machine clock is off. Check the key; make sure Windows time is set automatically. |
| **did not advance** / **not signed in** / unknown challenge | Google showed something the app cannot answer (phone prompt, security alert). Sign in by hand in normal Chrome once to clear it. |
| **consecutive job failures** | Gemini kept failing for that account (limit reached, refused prompts, page changed). Check `diagnostics\<account>\` and the log. |
| **Browser session lost** | The Chrome window crashed or was closed twice. Usually temporary; run again. |
| **not a valid proxy URL** (CSV will not load) | Fix the proxy in column 5 (see [2.2](#22-prepare-the-accounts-csv)). |

The batch always continues with the other accounts. Expect accounts to need signing in again from time to time — this is normal and handled automatically as long as the CSV is complete.

### A batch seems stuck

- A Gemini image can take a while. The app waits up to **180 seconds** per image, then retries (up to 3 times).
- Sign-ins happen **one at a time**. With several signed-out accounts, the others wait — up to 3 minutes each.
- Look at the **Status** and **Attempts** columns: if they change, it is working.
- Open the newest file in `logs\` to see what is happening right now.
- If nothing has changed for 10+ minutes: click **Stop**, wait for **Stopped**, then **Start** again. Finished images are kept.

### Many rows say "No accounts left to run this job"

Every account already had its turn, or the rest were quarantined. Fix or add accounts, then click **Start** again — finished images are skipped and the rest are retried.

### The RDP session locks or the windows stop working after disconnecting

The app drives real Chrome windows, which need an active, unlocked desktop. General Windows advice:

- Keep the Remote Desktop window **open and not minimized** while a batch runs.
- Turn off sleep: **Settings → System → Power** → set **Sleep** to **Never** on the RDP machine.
- If you must disconnect, ask your IT admin for the approved way to leave the session running unlocked (a common method is running `tscon` as administrator to hand the session to the console instead of closing the RDP window).
- If the session was locked mid-run, reconnect, then **Stop** and **Start** again if progress has frozen.

### Where the logs are

- `logs\geminibatch-YYYYMMDD.log` in the app folder — one file per day, the last 14 days are kept.
- `diagnostics\<account>\` — a screenshot (`.png`) and page description (`.aria.yml`) saved whenever an image fails.
- Send both to the developer when reporting a problem. Logs never contain passwords.

---

## 7. Quick reference

```text
GEMINI BATCH IMAGE GENERATOR — CHEAT SHEET

SETUP (once)
  • Chrome installed · run GeminiBatch.WinForms.exe
  • Accounts CSV:  email,password,recovery email,2FA key,proxy   (email required)
  • Accounts CSV… → pick file  (re-read automatically on every Start)

PROMPTS
  • Type: one per line, # = comment             → names: gemini_YYYYMMDD_HHMMSS.jpg
  • CSV:  Section,Image Prompt (+ any other cols) → saved in <output>\<Section>\
  • CSV:  Filename | Prompt   (header required)  → names: your filename (no extension)
  • Editing the box after loading a CSV drops the CSV filenames

RUN
  • Set Concurrency (3–5; max = number of accounts) → Start
  • Default: 1 image per account, random prompt → images = accounts
  • Hands off the Chrome windows · sign-in is automatic, one account at a time
  • Stop → wait for "Stopped" → Start later to resume

STATUS
  Pending · Running/Downloading (blue) · Retrying (amber)
  Completed (green) · Failed (red) · Skipped (grey = already done / duplicate)

RESULTS
  • Open output folder  (default: output\ next to the exe) · Browse… to change (remembered)
  • Never overwrites: name.jpg, name_2.jpg, name_3.jpg …
  • manifest.json = resume record. Same prompts + same account emails → resumes.
  • Fresh start: rename manifest.json

PROBLEMS
  • Summary box lists skipped accounts + reason → fix CSV / clear CAPTCHA in Chrome
  • Stuck > 10 min → Stop, Start
  • RDP: keep window open, no sleep, don't lock
  • Logs: logs\geminibatch-YYYYMMDD.log · failures: diagnostics\<account>\
  • Settings: appsettings.json → restart app
```
