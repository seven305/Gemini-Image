using GeminiBatch.Application;
using GeminiBatch.Application.Abstractions;
using GeminiBatch.Application.Models;
using GeminiBatch.Application.Options;
using GeminiBatch.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeminiBatch.WinForms;

/// <summary>
/// Code-behind for the batch UI. The controls and layout live in <c>MainForm.Designer.cs</c> (edit them
/// in the WinForms designer, not here); this partial holds the wiring and behavior only.
/// </summary>
public sealed partial class MainForm : Form
{
    private readonly BatchProcessor _processor = null!;
    private readonly IPromptSource _promptSource = null!;
    private readonly IAccountStore _accountStore = null!;
    private readonly ICsvAccountRoster _csvRoster = null!;
    private readonly IAccountLoginService _loginService = null!;
    private readonly BatchOptions _options = null!;
    private readonly ILogger<MainForm> _logger = null!;

    private readonly Dictionary<Guid, DataGridViewRow> _rowsByJob = new();
    private CancellationTokenSource? _cts;
    private bool _busy;

    // Accounts loaded from a CSV the operator picked; null = fall back to accounts.json.
    private IReadOnlyList<GeminiAccount>? _rosterAccounts;

    /// <summary>Designer-only constructor. The app always constructs <see cref="MainForm"/> through DI.</summary>
    public MainForm()
    {
        InitializeComponent();
    }

    public MainForm(
        BatchProcessor processor,
        IPromptSource promptSource,
        IAccountStore accountStore,
        ICsvAccountRoster csvRoster,
        IAccountLoginService loginService,
        IOptions<BatchOptions> options,
        ILogger<MainForm> logger)
        : this()
    {
        _processor = processor;
        _promptSource = promptSource;
        _accountStore = accountStore;
        _csvRoster = csvRoster;
        _loginService = loginService;
        _options = options.Value;
        _logger = logger;

        _numConcurrency.Value = Math.Clamp(_options.DefaultConcurrency, 1, 20);
    }

    // ---- Event handlers (wired in the designer) ---------------------------------------------

    private async void OnShown(object? sender, EventArgs e) => await RefreshAccountsAsync();
    private async void OnStartClick(object? sender, EventArgs e) => await StartAsync();
    private void OnStopClick(object? sender, EventArgs e) => Stop();
    private void OnLoadCsvClick(object? sender, EventArgs e) => LoadCsv();
    private async void OnLoginClick(object? sender, EventArgs e) => await LoginAsync();
    private void OnFormClosed(object? sender, FormClosedEventArgs e) => _cts?.Cancel();

    // ---- Behavior ---------------------------------------------------------------------------

    private async Task StartAsync()
    {
        var jobs = _promptSource.FromLines(_txtPrompts.Text);
        if (jobs.Count == 0)
        {
            MessageBox.Show(this, "Enter at least one prompt.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        IReadOnlyList<GeminiAccount> accounts;
        try
        {
            accounts = await CurrentAccountsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load accounts");
            MessageBox.Show(this, ex.Message, "Accounts", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        PopulateAccountPicker(accounts);
        PopulateGrid(jobs);
        SetRunning(true);
        _lblStatus.Text = $"Running {jobs.Count} job(s)…";

        // Progress<T> captures the UI SynchronizationContext here, so ApplyUpdate always runs on the UI thread.
        var progress = new Progress<JobUpdate>(ApplyUpdate);
        var concurrency = (int)_numConcurrency.Value;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var result = await Task.Run(() => _processor.RunAsync(jobs, accounts, concurrency, progress, ct), ct);
            _lblStatus.Text = $"Done — completed {result.Completed}, skipped {result.Skipped}, failed {result.Failed} of {result.Total}";
        }
        catch (OperationCanceledException)
        {
            _lblStatus.Text = "Stopped";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch crashed");
            _lblStatus.Text = "Error: " + ex.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    private void Stop()
    {
        _btnStop.Enabled = false;
        _lblStatus.Text = "Stopping…";
        _cts?.Cancel();
    }

    private void SetRunning(bool running)
    {
        _busy = running;
        _btnStart.Enabled = !running;
        _btnStop.Enabled = running;
        _txtPrompts.ReadOnly = running;
        _numConcurrency.Enabled = !running;
        _btnLogin.Enabled = !running;
        _btnLoadCsv.Enabled = !running;
        _cmbAccount.Enabled = !running;
    }

    /// <summary>Accounts come from the loaded CSV roster when present, otherwise from accounts.json.</summary>
    private async Task<IReadOnlyList<GeminiAccount>> CurrentAccountsAsync() =>
        _rosterAccounts ?? await _accountStore.LoadAsync(CancellationToken.None);

    /// <summary>
    /// Lets the operator pick a CSV whose first column is the account email. Sign-in stays manual: this
    /// only builds the account/profile roster; passwords and 2FA secrets in the file are not read.
    /// </summary>
    private void LoadCsv()
    {
        if (_busy) return;

        using var dialog = new OpenFileDialog
        {
            Title = "Select account CSV (first column = email)",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var accounts = _csvRoster.Read(dialog.FileName);
            _rosterAccounts = accounts;
            PopulateAccountPicker(accounts);
            _lblStatus.Text = $"Loaded {accounts.Count} account(s) from {Path.GetFileName(dialog.FileName)} — sign each in via Login…";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load account CSV {Path}", dialog.FileName);
            _lblStatus.Text = "CSV not loaded: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Load CSV", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Fills the account picker used by Login. Failures are non-fatal: Start reports them properly.</summary>
    private async Task RefreshAccountsAsync()
    {
        try
        {
            PopulateAccountPicker(await CurrentAccountsAsync());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load accounts for the picker");
            _lblStatus.Text = "Accounts not loaded: " + ex.Message;
        }
    }

    private void PopulateAccountPicker(IReadOnlyList<GeminiAccount> accounts)
    {
        var previous = (_cmbAccount.SelectedItem as AccountItem)?.Account.Id;
        _cmbAccount.Items.Clear();
        foreach (var account in accounts)
            _cmbAccount.Items.Add(new AccountItem(account));

        if (_cmbAccount.Items.Count == 0) return;
        var restored = _cmbAccount.Items.Cast<AccountItem>().ToList().FindIndex(a => a.Account.Id == previous);
        _cmbAccount.SelectedIndex = restored >= 0 ? restored : 0;
    }

    /// <summary>
    /// Opens the selected account's profile so a human can sign in (2FA included). The browser closes
    /// itself once the session is detected, which also flushes and unlocks the profile for the batch.
    /// </summary>
    private async Task LoginAsync()
    {
        if (_busy) return;
        if (_cmbAccount.SelectedItem is not AccountItem item)
        {
            MessageBox.Show(this, "No account selected. Check accounts.json.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetRunning(true);
        _cts = new CancellationTokenSource();
        var status = new Progress<string>(text => _lblStatus.Text = text);
        try
        {
            _lblStatus.Text = "Opening browser…";
            await _loginService.LoginInteractiveAsync(item.Account, TimeSpan.FromMinutes(15), status, _cts.Token);
            _lblStatus.Text = $"Signed in: {item.Account.Email}";
        }
        catch (OperationCanceledException)
        {
            _lblStatus.Text = "Login cancelled";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login failed for account {AccountId}", item.Account.Id);
            _lblStatus.Text = "Login failed: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Login", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    private sealed record AccountItem(GeminiAccount Account)
    {
        public override string ToString() => string.IsNullOrWhiteSpace(Account.Email) ? Account.Id : $"{Account.Id} ({Account.Email})";
    }

    private void PopulateGrid(IReadOnlyList<PromptJob> jobs)
    {
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        _rowsByJob.Clear();

        foreach (var job in jobs)
        {
            var index = _grid.Rows.Add(Preview(job.Prompt), job.DesiredFileName ?? "(auto)", job.Status.ToString(), string.Empty, "0");
            var row = _grid.Rows[index];
            row.Tag = job.Id;
            row.Cells["Prompt"].ToolTipText = job.Prompt;
            _rowsByJob[job.Id] = row;
        }

        _grid.ResumeLayout();
    }

    /// <summary>Updates only the affected row — no rebind, so the grid stays responsive under many updates.</summary>
    private void ApplyUpdate(JobUpdate update)
    {
        if (!_rowsByJob.TryGetValue(update.Id, out var row)) return;

        if (update.SavedPath is not null)
            row.Cells["Filename"].Value = Path.GetFileName(update.SavedPath);

        row.Cells["Status"].Value = update.Status.ToString();
        row.Cells["Account"].Value = update.AccountId ?? string.Empty;
        row.Cells["Attempts"].Value = update.Attempts.ToString();
        row.Cells["Status"].ToolTipText = update.Error ?? string.Empty;

        row.DefaultCellStyle.BackColor = update.Status switch
        {
            JobStatus.Completed => Color.FromArgb(225, 245, 225),
            JobStatus.Failed => Color.FromArgb(250, 220, 220),
            JobStatus.Retrying => Color.FromArgb(255, 243, 205),
            JobStatus.Skipped => Color.FromArgb(235, 235, 235),
            JobStatus.Running or JobStatus.Downloading => Color.FromArgb(220, 235, 250),
            _ => Color.White,
        };
    }

    private static string Preview(string prompt) => prompt.Length <= 80 ? prompt : prompt[..80] + "…";
}
