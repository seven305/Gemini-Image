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
    private readonly PromptPlanner _planner = null!;
    private readonly IPromptSource _promptSource = null!;
    private readonly ICsvAccountRoster _csvRoster = null!;
    private readonly BatchOptions _options = null!;
    private readonly ILogger<MainForm> _logger = null!;

    private readonly Dictionary<Guid, DataGridViewRow> _rowsByJob = new();
    private CancellationTokenSource? _cts;
    private bool _busy;

    // The account CSV the operator picked (re-read on every Start so edits and fresh state are picked up);
    // null = none chosen yet, so Start asks for one.
    private string? _rosterCsvPath;

    /// <summary>Designer-only constructor. The app always constructs <see cref="MainForm"/> through DI.</summary>
    public MainForm()
    {
        InitializeComponent();
    }

    public MainForm(
        BatchProcessor processor,
        PromptPlanner planner,
        IPromptSource promptSource,
        ICsvAccountRoster csvRoster,
        IOptions<BatchOptions> options,
        ILogger<MainForm> logger)
        : this()
    {
        _processor = processor;
        _planner = planner;
        _promptSource = promptSource;
        _csvRoster = csvRoster;
        _options = options.Value;
        _logger = logger;

        _numConcurrency.Value = Math.Clamp(_options.DefaultConcurrency, 1, 20);
    }

    // ---- Event handlers (wired in the designer) ---------------------------------------------

    private async void OnStartClick(object? sender, EventArgs e) => await StartAsync();
    private void OnStopClick(object? sender, EventArgs e) => Stop();
    private void OnLoadCsvClick(object? sender, EventArgs e) => LoadCsv();
    private void OnFormClosed(object? sender, FormClosedEventArgs e) => _cts?.Cancel();

    // ---- Behavior ---------------------------------------------------------------------------

    private async Task StartAsync()
    {
        var prompts = _promptSource.FromLines(_txtPrompts.Text);
        if (prompts.Count == 0)
        {
            MessageBox.Show(this, "Enter at least one prompt.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Credentials always come from the CSV: ask for it the first time, then re-read it on every Start so
        // edits are picked up and no account carries a quarantine over from the previous run.
        var csvPath = _rosterCsvPath ?? PromptForCsv();
        if (csvPath is null)
        {
            _lblStatus.Text = "Start cancelled — no account CSV";
            return;
        }

        var accounts = LoadRoster(csvPath);
        if (accounts is null) return;

        // One job per account turn, each with a randomly picked prompt (unless RandomizePrompts is off).
        var jobs = _planner.Plan(prompts, accounts);
        PopulateGrid(jobs);
        SetRunning(true);
        // Each worker signs its account in (one at a time, app-wide) when its profile turns out to be signed out.
        _lblStatus.Text = $"Signing in accounts and running {jobs.Count} image(s) from {prompts.Count} prompt(s)…";

        // Progress<T> captures the UI SynchronizationContext here, so ApplyUpdate always runs on the UI thread.
        var progress = new Progress<JobUpdate>(ApplyUpdate);
        var concurrency = (int)_numConcurrency.Value;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var result = await Task.Run(() => _processor.RunAsync(jobs, accounts, concurrency, progress, ct), ct);
            _lblStatus.Text = $"Done — completed {result.Completed}, skipped {result.Skipped}, failed {result.Failed} of {result.Total}";
            if (result.StopReason == BatchStopReason.AllAccountsQuarantined)
                _lblStatus.Text += " · stopped early: all accounts quarantined";
            else if (result.StopReason == BatchStopReason.AccountsExhausted)
                _lblStatus.Text += " · stopped early: all accounts used";
            ReportSkippedAccounts(result.QuarantinedAccounts);
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
        _btnLoadCsv.Enabled = !running;
    }

    private void LoadCsv()
    {
        if (_busy) return;
        if (PromptForCsv() is { } path)
            LoadRoster(path);
    }

    /// <summary>Asks for the account CSV (email, password, recovery email, 2FA key, proxy). Null when cancelled.</summary>
    private string? PromptForCsv()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select account CSV (email, password, recovery email, 2FA key, proxy)",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    /// <summary>
    /// Reads the CSV into fresh accounts and remembers the path. The credentials stay in memory for automated
    /// sign-in; a row without a password can only be used if its profile is already signed in. Returns null
    /// (after telling the operator) on error.
    /// </summary>
    private IReadOnlyList<GeminiAccount>? LoadRoster(string csvPath)
    {
        try
        {
            var accounts = _csvRoster.Read(csvPath);
            _rosterCsvPath = csvPath;
            var automated = accounts.Count(a => a.Credentials is not null);
            _lblStatus.Text = $"Loaded {accounts.Count} account(s) from {Path.GetFileName(csvPath)} ({automated} with credentials)";
            return accounts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load account CSV {Path}", csvPath);
            _lblStatus.Text = "CSV not loaded: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Load CSV", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
    }

    /// <summary>Tells the operator which accounts the batch set aside (failed sign-in, repeated failures) and why.</summary>
    private void ReportSkippedAccounts(IReadOnlyList<QuarantinedAccount> skipped)
    {
        if (skipped.Count == 0) return;

        _lblStatus.Text += $" · {skipped.Count} account(s) skipped (see details)";
        var details = string.Join(Environment.NewLine + Environment.NewLine, skipped.Select(a => $"{a.Email}: {a.Reason}"));
        MessageBox.Show(this, details, "Skipped accounts", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
