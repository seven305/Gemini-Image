// Spike: Gemini web automation feasibility. Throwaway code — one account, one prompt, one image.
//
//   dotnet run -- login      first run: pause so a human logs in (incl. 2FA); profile persists in ./profile
//   dotnet run -- inspect    dump ARIA/DOM snapshots of the Gemini UI to ./out for locator discovery
//   dotnet run -- generate   submit a hard-coded image prompt, wait for the image, download it to ./out
//
// Set GEMINI_CHANNEL=chrome to launch the installed Chrome instead of Playwright's bundled Chromium
// (required on this machine: bundled Chromium fails with "spawn UNKNOWN"). See SPIKE_FINDINGS.md.

using System.Diagnostics;
using Microsoft.Playwright;

const string GeminiUrl = "https://gemini.google.com/app";
const string Prompt = "A watercolor painting of a lighthouse at dawn, soft pastel sky, no text";

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "generate";
var profileDir = Path.GetFullPath("profile");
var outDir = Path.GetFullPath("out");
Directory.CreateDirectory(outDir);

var channel = Environment.GetEnvironmentVariable("GEMINI_CHANNEL"); // null => bundled Chromium
var total = Stopwatch.StartNew();
IPage? page = null;

Console.WriteLine($"mode={mode} channel={channel ?? "chromium(bundled)"} profile={profileDir}");

using var pw = await Playwright.CreateAsync();
IBrowserContext ctx = await LaunchAsync();

try
{
    await Step("navigate", Navigate);

    switch (mode)
    {
        case "login": await LoginMode(); break;
        case "inspect": await InspectMode(); break;
        case "generate": await GenerateMode(); break;
        default: throw new ArgumentException($"unknown mode '{mode}'");
    }

    Console.WriteLine($"TOTAL {total.Elapsed.TotalSeconds:F1}s");
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED after {total.Elapsed.TotalSeconds:F1}s: {ex.GetType().Name}: {ex.Message}");
    Environment.ExitCode = 1;
}
finally
{
    try { await ctx.CloseAsync(); } catch (PlaywrightException) { } // flush the persistent profile (browser may already be gone)
}

// ---------------------------------------------------------------------------------------------

async Task<IBrowserContext> LaunchAsync()
{
    var c = await pw.Chromium.LaunchPersistentContextAsync(profileDir, new()
    {
        Headless = false,
        Channel = channel,
        AcceptDownloads = true,
        ViewportSize = ViewportSize.NoViewport,
        IgnoreDefaultArgs = ["--enable-automation"],
        Args = ["--disable-blink-features=AutomationControlled", "--start-maximized"],
    });
    page = c.Pages.Count > 0 ? c.Pages[0] : await c.NewPageAsync();
    page.SetDefaultTimeout(30_000);
    return c;
}

async Task Navigate()
{
    await page!.GotoAsync(GeminiUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
    await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 20_000 }).ContinueWith(_ => { });
    Console.WriteLine($"  url after nav: {page.Url}");
}

// No Inspector/Resume: poll until the page is signed in, then close, relaunch and re-check persistence in one go.
async Task LoginMode()
{
    Console.WriteLine("  >>> Log in manually in the Chrome window (including 2FA). This run auto-detects sign-in; do NOT close the window.");
    await Step("wait for manual sign-in (up to 15 min)", async () =>
    {
        var deadline = DateTime.UtcNow.AddMinutes(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsSignedIn()) return;
            await Task.Delay(2000);
        }
        throw new TimeoutException("no sign-in detected within 15 min");
    });
    await Step("flush profile (close browser)", async () =>
    {
        await page!.WaitForTimeoutAsync(5000); // give Chrome a moment to write cookies
        await ctx.CloseAsync();
    });
    await Step("relaunch + verify persistence", async () =>
    {
        ctx = await LaunchAsync();
        await Navigate();
        await AssertSignedIn();
    });
    Console.WriteLine("  login PERSISTED across relaunch.");
}

async Task<bool> IsSignedIn()
{
    if (page!.Url.Contains("accounts.google.com")) return false;
    if (!page.Url.StartsWith("https://gemini.google.com")) return false;
    var cookies = await ctx.CookiesAsync(["https://gemini.google.com"]);
    var hasSid = cookies.Any(c => c.Name is "SID" or "__Secure-1PSID" or "__Secure-3PSID");
    var signInVisible = await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).CountAsync() > 0;
    return hasSid && !signInVisible;
}

async Task InspectMode()
{
    await Step("assert logged in", AssertSignedIn);
    await Step("snapshot 1 (idle)", () => Snapshot("1-idle"));

    await Step("snapshot 2 (model picker open)", async () =>
    {
        // Model picker is a button in the top bar; name discovered from snapshot 1. Try a few candidates.
        var picker = page!.Locator("[data-test-id='bard-mode-menu-button'], button:has-text('Gemini')").First;
        if (await picker.CountAsync() > 0 && await picker.IsVisibleAsync())
        {
            await picker.ClickAsync();
            await page.WaitForTimeoutAsync(1000);
            await Snapshot("2-model-menu");
            await page.Keyboard.PressAsync("Escape");
        }
        else Console.WriteLine("  (no model picker candidate found)");
    });

    await Step("snapshot 3 (tools menu open)", async () =>
    {
        var tools = page!.GetByRole(AriaRole.Button, new() { NameRegex = new("tools", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).First;
        if (await tools.CountAsync() > 0 && await tools.IsVisibleAsync())
        {
            await tools.ClickAsync();
            await page.WaitForTimeoutAsync(1000);
            await Snapshot("3-tools-menu");
            await page.Keyboard.PressAsync("Escape");
        }
        else Console.WriteLine("  (no tools button candidate found)");
    });

    await Step("snapshot 4 (prompt typed)", async () =>
    {
        await PromptBox().ClickAsync();
        await page!.Keyboard.TypeAsync("probe");
        await page.WaitForTimeoutAsync(500);
        await Snapshot("4-prompt-typed");
    });

    Console.WriteLine("  >>> Pausing so you can poke around. Press Resume to exit.");
    await page!.PauseAsync();
}

async Task GenerateMode()
{
    await Step("assert logged in", AssertSignedIn);

    // Log every image/binary response so we can see where the pixels actually come from (network-capture option).
    var generatedResponses = new List<IResponse>();
    page!.Response += (_, r) =>
    {
        var ct = r.Headers.TryGetValue("content-type", out var v) ? v : "";
        if (!ct.StartsWith("image/")) return;
        if (r.Url.Contains("googleusercontent.com/rd-gg-dl/")) generatedResponses.Add(r); // generated image CDN
        if (!r.Url.Contains("gstatic.com")) Console.WriteLine($"  [net] {r.Status} {ct} {r.Url}");
    };

    await Step("dismiss 'Keep in mind' notice", async () =>
    {
        var gotIt = page!.GetByRole(AriaRole.Button, new() { Name = "Got it", Exact = true });
        if (await gotIt.CountAsync() > 0) await gotIt.ClickAsync();
    });

    await Step("select 'Create image' tool", async () =>
    {
        await page!.GetByRole(AriaRole.Button, new() { Name = "Upload & tools" }).ClickAsync();
        var createImage = page.GetByRole(AriaRole.Menuitemcheckbox, new() { Name = "Create image" });
        await createImage.WaitForAsync();
        await createImage.ClickAsync();
        await page.WaitForTimeoutAsync(500);
        await Snapshot("5-image-tool-selected"); // records what the UI shows once the tool is on
    });

    await Step("type prompt", async () =>
    {
        var box = PromptBox();
        await box.ClickAsync();
        await box.FillAsync(Prompt);
    });

    await Step("submit", async () =>
    {
        await page!.GetByRole(AriaRole.Button, new() { Name = "Send message" }).ClickAsync();
    });

    ILocator? image = null;
    await Step("wait for generated image", async () =>
    {
        // Discovered (run #1): <model-response> ... <generated-image> <img class="image animate loaded" alt=", AI generated" src="blob:...">.
        // Note: the image appears while the "Stop response" button is still shown; the img gets class "loaded" once decoded.
        image = page!.Locator("model-response").Last.Locator("generated-image img.loaded").First;
        try
        {
            await image.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 180_000 });
        }
        finally
        {
            await Snapshot("6-response");
        }
        var src = await image.GetAttributeAsync("src");
        Console.WriteLine($"  image src: {src?[..Math.Min(80, src.Length)]}...");
    });

    await Step("wait for response complete ('Stop response' gone)", async () =>
    {
        // The image shows up while the turn is still streaming; wait until the send button is back.
        await page!.GetByRole(AriaRole.Button, new() { Name = "Stop response" }).WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 120_000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Download full size image" }).First.WaitForAsync(new() { Timeout = 30_000 });
    });

    var downloadMode = Environment.GetEnvironmentVariable("GEMINI_DOWNLOAD") ?? "net"; // net (default, crash-free) | ui (Download event; crashes Chromium 153 ~60%) | blob (canvas preview)
    if (downloadMode == "net")
    {
        await Step("download (network capture of generated-image CDN response)", async () =>
        {
            var sized = new List<(IResponse r, byte[] body)>();
            foreach (var r in generatedResponses) sized.Add((r, await r.BodyAsync()));
            foreach (var (r, body) in sized) Console.WriteLine($"  candidate {body.Length / 1024} KB  {r.Url}");
            var best = sized.OrderByDescending(x => x.body.Length).First();
            var target = Path.Combine(outDir, $"gemini-net-{DateTime.Now:yyyyMMdd-HHmmss}.jpg");
            await File.WriteAllBytesAsync(target, best.body);
            Console.WriteLine($"  saved {target} ({best.body.Length / 1024} KB) from {best.r.Url}");

            // Try the "original size" variant of the same URL (Google image CDN size suffix => =s0).
            var noQuery = best.r.Url.Split('?')[0];            // ...rd-gg-dl/<id>=s1024-rj?alr=yes
            var idx = noQuery.LastIndexOf('=');
            var fullUrl = (idx > 0 ? noQuery[..idx] : noQuery) + "=s0"; // ...rd-gg-dl/<id>=s0
            var resp = await ctx.APIRequest.GetAsync(fullUrl);
            Console.WriteLine($"  =s0 fetch: {resp.Status} {resp.Headers.GetValueOrDefault("content-type")}");
            if (resp.Ok)
            {
                var bytes = await resp.BodyAsync();
                var t2 = Path.Combine(outDir, $"gemini-net-s0-{DateTime.Now:yyyyMMdd-HHmmss}.jpg");
                await File.WriteAllBytesAsync(t2, bytes);
                Console.WriteLine($"  saved {t2} ({bytes.Length / 1024} KB)");
            }
        });
        return;
    }
    if (downloadMode == "blob")
    {
        await Step("download (in-page blob fetch fallback)", async () =>
        {
            // fetch(blob:) fails ("Failed to fetch" — Gemini revokes the blob URL once the <img> has loaded),
            // so re-encode the decoded <img> through a canvas instead (same-origin blob => canvas not tainted).
            var b64 = await image!.EvaluateAsync<string>(@"img => {
                const c = document.createElement('canvas'); c.width = img.naturalWidth; c.height = img.naturalHeight;
                c.getContext('2d').drawImage(img, 0, 0);
                return c.toDataURL('image/png').split(',')[1]; }");
            var bytes = Convert.FromBase64String(b64);
            var ext = ".png";
            Console.WriteLine($"  naturalWidth x naturalHeight = {await image.EvaluateAsync<string>("img => img.naturalWidth + 'x' + img.naturalHeight")}");
            var target = Path.Combine(outDir, $"gemini-blob-{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
            await File.WriteAllBytesAsync(target, bytes);
            Console.WriteLine($"  saved {target} ({bytes.Length / 1024} KB)");
        });
        return;
    }

    await Step("download", async () =>
    {
        await image!.HoverAsync();
        await page!.WaitForTimeoutAsync(300);
        await Snapshot("7-image-hover");
        var download = await page.RunAndWaitForDownloadAsync(async () =>
        {
            // Discovered (run #1): <download-generated-image-button> > button[aria-label="Download full size image"] (data-test-id="download-generated-image-button")
            await page.GetByRole(AriaRole.Button, new() { Name = "Download full size image" }).First.ClickAsync();
        }, new() { Timeout = 60_000 });
        var target = Path.Combine(outDir, $"gemini-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(download.SuggestedFilename)}");
        await download.SaveAsAsync(target);
        Console.WriteLine($"  suggested name: {download.SuggestedFilename}");
        Console.WriteLine($"  saved {target} ({new FileInfo(target).Length / 1024} KB)");
    });
}

// ---------------------------------------------------------------------------------------------

ILocator PromptBox() => page!.GetByRole(AriaRole.Textbox, new() { Name = "Enter a prompt for Gemini" });

// Signed-out Gemini still renders the prompt box, so check cookies + absence of the "Sign in" button.
async Task AssertSignedIn()
{
    if (page!.Url.Contains("accounts.google.com")) throw new Exception("redirected to Google sign-in; session not persisted");
    await PromptBox().WaitForAsync(new() { Timeout = 60_000 });
    var cookies = await ctx.CookiesAsync(["https://gemini.google.com"]);
    var sid = cookies.Where(c => c.Name is "SID" or "__Secure-1PSID" or "__Secure-3PSID").Select(c => c.Name).ToList();
    Console.WriteLine($"  google cookies: {cookies.Count} total, session cookies: [{string.Join(",", sid)}]");
    var signIn = page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true });
    if (await signIn.CountAsync() > 0 || sid.Count == 0)
        throw new Exception("NOT signed in: 'Sign in' button present / no SID cookie");
}

async Task Snapshot(string tag)
{
    var aria = await page!.Locator("body").AriaSnapshotAsync();
    await File.WriteAllTextAsync(Path.Combine(outDir, $"aria-{tag}.yml"), aria);
    await File.WriteAllTextAsync(Path.Combine(outDir, $"dom-{tag}.html"), await page.ContentAsync());
    await page.ScreenshotAsync(new() { Path = Path.Combine(outDir, $"shot-{tag}.png") });
    Console.WriteLine($"  wrote aria/dom/shot-{tag}");
}

async Task Step(string name, Func<Task> body)
{
    var sw = Stopwatch.StartNew();
    Console.WriteLine($"[{total.Elapsed.TotalSeconds,6:F1}s] {name} ...");
    try
    {
        await body();
        Console.WriteLine($"[{total.Elapsed.TotalSeconds,6:F1}s] {name} done ({sw.ElapsedMilliseconds} ms)");
    }
    catch
    {
        var shot = Path.Combine(outDir, $"fail-{name.Replace(' ', '-')}.png");
        try { await page!.ScreenshotAsync(new() { Path = shot, FullPage = true }); Console.WriteLine($"  screenshot: {shot}"); } catch { }
        throw;
    }
}
