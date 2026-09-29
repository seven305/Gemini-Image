using GeminiBatch.Domain;
using GeminiBatch.Infrastructure.Accounts;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace GeminiBatch.Infrastructure.Gemini;

/// <summary>
/// Drives Google's sign-in pages with the account's CSV credentials: email → password → authenticator
/// code (computed from the 2FA key) / recovery-email confirmation → Gemini. It is a poll loop, not a
/// script: each tick looks at which step is on screen and acts on it, so it copes with steps appearing in
/// any order or being skipped (e.g. a profile that only needs the password again).
///
/// It never fights Google: a CAPTCHA, an unknown challenge, or a step that fails twice (wrong password,
/// rejected code) ends the automation and the caller gets <c>false</c> with <see cref="StopReason"/> set.
/// Secrets are typed into the page and never logged.
/// </summary>
internal sealed class GoogleSignInDriver
{
    private const int MaxAttemptsPerStep = 2;

    /// <summary>Starting the flow may legitimately repeat, e.g. an interstitial that lands on myaccount instead of Gemini.</summary>
    private const int MaxSignInPageOpens = 3;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan StepTransitionTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Don't submit a code with less than this left in its 30 s window — Google may reject it on arrival.</summary>
    private static readonly TimeSpan MinTotpValidity = TimeSpan.FromSeconds(5);

    private readonly GeminiAccount _account;
    private readonly ILogger _logger;
    private readonly Dictionary<Step, int> _attempts = new();
    private long _lastTotpStep = -1;
    private DateTime _lastSignInNavigation = DateTime.MinValue;

    private enum Step { SignInPage, AccountChooser, Email, Password, Totp, RecoveryEmail, ChallengePicker, TryAnotherWay, Interstitial }

    public GoogleSignInDriver(GeminiAccount account, ILogger logger)
    {
        _account = account;
        _logger = logger;
    }

    /// <summary>Why automation gave up (CAPTCHA, rejected password, …); null while it is still driving or succeeded.</summary>
    public string? StopReason { get; private set; }

    /// <summary>
    /// Returns true once the context holds a signed-in Gemini session, false as soon as automation gets
    /// stuck (or at once when the account has no credentials). Throws <see cref="TimeoutException"/> when
    /// <paramref name="timeout"/> elapses.
    /// </summary>
    public async Task<bool> SignInAsync(IBrowserContext context, IPage page, TimeSpan timeout, CancellationToken ct)
    {
        if (_account.Credentials is null)
        {
            StopReason = "the CSV has no password for this account.";
            return false;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (page.IsClosed)
                throw new InvalidOperationException("The browser window was closed before sign-in completed.");

            var state = await GeminiSignInCheck.ProbeAsync(context, page).ConfigureAwait(false);
            if (state.IsSignedIn)
                return true;

            var stuckReason = await TryAdvanceAsync(page, state, ct).ConfigureAwait(false);
            if (stuckReason is not null)
            {
                StopReason = stuckReason;
                _logger.LogWarning("Automated sign-in for {AccountId} stopped: {Reason}", _account.Id, stuckReason);
                return false;
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        throw new TimeoutException($"No sign-in detected for {_account.Id} within {timeout.TotalMinutes:F0} minutes.");
    }

    /// <summary>Acts on whatever sign-in step is showing. Returns a reason when automation should stop, else null.</summary>
    private async Task<string?> TryAdvanceAsync(IPage page, GeminiSignInCheck.SignInState state, CancellationToken ct)
    {
        var credentials = _account.Credentials!;
        try
        {
            if (!GeminiSelectors.IsLoginRedirect(page.Url))
            {
                // Signed-out Gemini (or a stray page): start the Google flow. Throttled so a slow redirect
                // is not restarted every tick.
                if (DateTime.UtcNow - _lastSignInNavigation < StepTransitionTimeout) return null;
                if (!Attempt(Step.SignInPage)) return "Google sign-in did not start (still not on the sign-in page).";
                _lastSignInNavigation = DateTime.UtcNow;
                _logger.LogInformation("Opening Google sign-in for {AccountId} (was {Url})", _account.Id, state.Url);
                await page.GotoAsync(GeminiSelectors.SignInUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct).ConfigureAwait(false);
                return null;
            }

            if (await IsVisibleAsync(GeminiSelectors.SignInCaptcha(page)).ConfigureAwait(false))
                return "Google is showing a CAPTCHA.";

            if (await IsVisibleAsync(GeminiSelectors.SignInTotpInput(page)).ConfigureAwait(false))
            {
                if (!credentials.HasTotp) return "Google asks for an authenticator code but the CSV has no 2FA key.";
                return await SubmitTotpAsync(page, credentials.TotpSecret!, ct).ConfigureAwait(false);
            }

            if (await IsVisibleAsync(GeminiSelectors.SignInRecoveryEmailInput(page)).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(credentials.RecoveryEmail)) return "Google asks to confirm the recovery email but the CSV has none.";
                return await SubmitAsync(page, Step.RecoveryEmail, GeminiSelectors.SignInRecoveryEmailInput(page), credentials.RecoveryEmail, ct).ConfigureAwait(false);
            }

            if (await IsVisibleAsync(GeminiSelectors.SignInPasswordInput(page)).ConfigureAwait(false))
                return await SubmitAsync(page, Step.Password, GeminiSelectors.SignInPasswordInput(page), credentials.Password, ct).ConfigureAwait(false);

            if (await IsVisibleAsync(GeminiSelectors.SignInEmailInput(page)).ConfigureAwait(false))
                return await SubmitAsync(page, Step.Email, GeminiSelectors.SignInEmailInput(page), _account.Email, ct).ConfigureAwait(false);

            var chooserEntry = GeminiSelectors.SignInAccountChooserEntry(page, _account.Email);
            if (await IsVisibleAsync(chooserEntry).ConfigureAwait(false))
                return await ClickAsync(Step.AccountChooser, chooserEntry, ct).ConfigureAwait(false);

            // Challenge picker: prefer authenticator codes, then recovery email.
            var authenticator = GeminiSelectors.SignInAuthenticatorOption(page);
            if (credentials.HasTotp && await IsVisibleAsync(authenticator).ConfigureAwait(false))
                return await ClickAsync(Step.ChallengePicker, authenticator, ct).ConfigureAwait(false);

            var recovery = GeminiSelectors.SignInRecoveryEmailOption(page);
            if (!string.IsNullOrWhiteSpace(credentials.RecoveryEmail) && await IsVisibleAsync(recovery).ConfigureAwait(false))
                return await ClickAsync(Step.ChallengePicker, recovery, ct).ConfigureAwait(false);

            // A 2SV page we can't answer (phone prompt, SMS): ask for the picker once.
            var tryAnother = GeminiSelectors.SignInTryAnotherWayButton(page);
            if (await IsVisibleAsync(tryAnother).ConfigureAwait(false))
                return await ClickAsync(Step.TryAnotherWay, tryAnother, ct).ConfigureAwait(false);

            var skip = GeminiSelectors.SignInSkipInterstitialButton(page);
            if (await IsVisibleAsync(skip).ConfigureAwait(false))
                return await ClickAsync(Step.Interstitial, skip, ct).ConfigureAwait(false);

            // Nothing recognizable yet — usually a page mid-transition. The overall timeout bounds this.
            return null;
        }
        catch (PlaywrightException ex)
        {
            // Navigation mid-action detaches elements; the next tick re-reads the page.
            _logger.LogDebug(ex, "Sign-in step interrupted for {AccountId}; re-checking", _account.Id);
            return null;
        }
    }

    private async Task<string?> SubmitTotpAsync(IPage page, string secret, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (TotpGenerator.RemainingInStep(now) < MinTotpValidity)
        {
            await Task.Delay(TotpGenerator.RemainingInStep(now) + TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            now = DateTimeOffset.UtcNow;
        }

        string code;
        long timeStep;
        try
        {
            (code, timeStep) = TotpGenerator.Compute(secret, now);
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }

        // Google rejects a code that was already used; a retry needs the next window.
        if (timeStep == _lastTotpStep)
        {
            await Task.Delay(TotpGenerator.RemainingInStep(now) + TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            (code, timeStep) = TotpGenerator.Compute(secret, DateTimeOffset.UtcNow);
        }

        _lastTotpStep = timeStep;
        return await SubmitAsync(page, Step.Totp, GeminiSelectors.SignInTotpInput(page), code, ct).ConfigureAwait(false);
    }

    /// <summary>Types <paramref name="value"/> at a human pace, presses Enter, and waits for the field to go away.</summary>
    private async Task<string?> SubmitAsync(IPage page, Step step, ILocator input, string value, CancellationToken ct)
    {
        if (!Attempt(step))
            return $"the {Describe(step)} step was rejected {MaxAttemptsPerStep} times.";

        _logger.LogInformation("Sign-in {AccountId}: entering {Step} (attempt {Attempt})", _account.Id, Describe(step), _attempts[step]);

        await input.ClickAsync().WaitAsync(ct).ConfigureAwait(false);
        await input.FillAsync(string.Empty).WaitAsync(ct).ConfigureAwait(false);
        await input.PressSequentiallyAsync(value, new() { Delay = Random.Shared.Next(60, 140) }).WaitAsync(ct).ConfigureAwait(false);
        await Task.Delay(Random.Shared.Next(300, 800), ct).ConfigureAwait(false);
        await input.PressAsync("Enter").WaitAsync(ct).ConfigureAwait(false);

        await WaitForStepToLeaveAsync(input, ct).ConfigureAwait(false);
        return null;
    }

    private async Task<string?> ClickAsync(Step step, ILocator target, CancellationToken ct)
    {
        if (!Attempt(step))
            return $"the {Describe(step)} step did not advance after {MaxAttemptsPerStep} tries.";

        _logger.LogInformation("Sign-in {AccountId}: {Step}", _account.Id, Describe(step));
        await Task.Delay(Random.Shared.Next(300, 800), ct).ConfigureAwait(false);
        await target.ClickAsync().WaitAsync(ct).ConfigureAwait(false);
        await WaitForStepToLeaveAsync(target, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Waits for the element just acted on to disappear, so the next tick doesn't re-submit a page that is
    /// still loading. If it stays (e.g. "Wrong password"), the next tick counts a second attempt.
    /// </summary>
    private static async Task WaitForStepToLeaveAsync(ILocator locator, CancellationToken ct)
    {
        try
        {
            await locator.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = (float)StepTransitionTimeout.TotalMilliseconds })
                .WaitAsync(ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }

    private bool Attempt(Step step)
    {
        var count = _attempts.GetValueOrDefault(step) + 1;
        _attempts[step] = count;
        return count <= (step == Step.SignInPage ? MaxSignInPageOpens : MaxAttemptsPerStep);
    }

    private static async Task<bool> IsVisibleAsync(ILocator locator)
    {
        try { return await locator.IsVisibleAsync().ConfigureAwait(false); }
        catch (PlaywrightException) { return false; }
    }

    private static string Describe(Step step) => step switch
    {
        Step.SignInPage => "open sign-in",
        Step.AccountChooser => "choose account",
        Step.Email => "email",
        Step.Password => "password",
        Step.Totp => "2FA code",
        Step.RecoveryEmail => "recovery email",
        Step.ChallengePicker => "choose verification method",
        Step.TryAnotherWay => "try another way",
        Step.Interstitial => "skip prompt",
        _ => step.ToString(),
    };
}
