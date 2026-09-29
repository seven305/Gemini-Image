using System.Diagnostics;
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
    private static readonly Color CompletedColor = Color.FromArgb(200, 235, 200);
    private static readonly Color FailedColor = Color.FromArgb(245, 195, 195);
    private static readonly Color RetryingColor = Color.FromArgb(255, 230, 170);
    private static readonly Color RunningColor = Color.FromArgb(205, 225, 250);
    private static readonly Color SkippedColor = Color.FromArgb(225, 225, 225);

    private readonly BatchProcessor _processor = null!;
    private readonly PromptPlanner _planner = null!;
    private readonly IPromptSource _promptSource = null!;
    private readonly ICsvAccountRoster _csvRoster = null!;
    private readonly BatchOptions _options = null!;
    private readonly ILogger<MainForm> _logger = null!;

    private readonly Dictionary<Guid, DataGridViewRow> _rowsByJob = new();
    private readonly Dictionary<Guid, JobStatus> _statusByJob = new();
    // Jobs the manifest reported as already done by an earlier run (Skipped without an error).
    private readonly HashSet<Guid> _resumedJobs = new();
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _stopping;

    // The account CSV the operator picked (re-read on every Start so edits and fresh state are picked up);
    // null = none chosen yet, so Start asks for one. _roster is its last successful read, for the preview and cap.
    private string? _rosterCsvPath;
    private IReadOnlyList<GeminiAccount>? _roster;

    // Prompts loaded from a prompts CSV (with file names). Null = the text box is the source; editing the text box
    // switches back to it. _settingPromptsText suppresses that switch while the CSV fills the box.
    private IReadOnlyList<PromptJob>? _csvPrompts;
    private string? _promptCsvName;
    private bool _settingPromptsText;

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

        _numConcurrency.Value = Math.Clamp(_options.DefaultConcurrency, 1, (int)_numConcurrency.Maximum);
        _txtOutputFolder.Text = TryGetOutputFolder(out var folder, out _) ? folder : "(not set — configure Batch:OutputFolder)";
    }

    // ---- Event handlers (wired in the designer) ---------------------------------------------

    private async void OnStartClick(object? sender, EventArgs e) => await StartAsync();
    private void OnStopClick(object? sender, EventArgs e) => Stop();
    private void OnLoadCsvClick(object? sender, EventArgs e) => LoadCsv();
    private void OnLoadPromptsClick(object? sender, EventArgs e) => LoadPromptsCsv();
    private void OnOpenOutputClick(object? sender, EventArgs e) => OpenOutputFolder();
    private void OnFormClosed(object? sender, FormClosedEventArgs e) => _cts?.Cancel();

    private void OnPromptsTextChanged(object? sender, EventArgs e)
    {
        if (_settingPromptsText || _busy) return;
        // The operator typed or pasted: the text box is the prompt source again.
        _csvPrompts = null;
        _promptCsvName = null;
        RefreshPromptPreview();
    }

    // ---- Behavior ---------------------------------------------------------------------------

    private async Task StartAsync()
    {
        var prompts = CurrentPrompts();
        if (prompts.Count == 0)
        {
            ShowValidation("Enter at least one prompt (one per line), or load a prompts CSV.");
            return;
        }

        if (!TryGetOutputFolder(out var outputFolder, out var folderError))
        {
            ShowValidation("The output folder is not usable: " + folderError);
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
        if (!accounts.Any(a => a.Enabled))
        {
            ShowValidation($"No enabled accounts in {Path.GetFileName(csvPath)}. Add at least one account row.");
            return;
        }

        // One job per account turn, each with a randomly picked prompt (unless RandomizePrompts is off).
        var jobs = _planner.Plan(prompts, accounts);
        if (jobs.Count == 0)
        {
            ShowValidation("Nothing to run: no usable accounts after removing duplicates.");
            return;
        }

        PopulateGrid(jobs);
        SetRunning(true);
        var started = DateTime.Now;
        _txtRunSummary.Text = $"Run started {started:HH:mm:ss} — {jobs.Count} image(s) from {prompts.Count} prompt(s), output: {outputFolder}";
        // Each worker signs its account in (one at a time, app-wide) when its profile turns out to be signed out.
        _lblStatus.Text = $"Signing in accounts and running {jobs.Count} image(s)…";

        // Progress<T> captures the UI SynchronizationContext here, so ApplyUpdate always runs on the UI thread.
        var progress = new Progress<JobUpdate>(ApplyUpdate);
        var concurrency = (int)_numConcurrency.Value;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var result = await Task.Run(() => _processor.RunAsync(jobs, accounts, concurrency, progress, ct), ct);
            ReportFinished(result);
        }
        catch (OperationCanceledException)
        {
            var (completed, failed, skipped, _) = CountStatuses();
            var notProcessed = _statusByJob.Count - completed - failed - skipped;
            _lblStatus.Text = $"Stopped — {completed} completed · {skipped} skipped · {failed} failed · {notProcessed} not processed";
            _txtRunSummary.Text = $"Run stopped {DateTime.Now:HH:mm:ss} — {completed} completed · {skipped} skipped · {failed} failed · "
                + $"{notProcessed} not processed of {_statusByJob.Count}. Start again to resume; finished images are skipped.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch crashed");
            _lblStatus.Text = "Error: " + ex.Message;
            _txtRunSummary.Text = $"Run failed {DateTime.Now:HH:mm:ss}: {ex.Message} (details in the log file)";
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
        _stopping = true;
        _btnStop.Enabled = false;
        _lblStatus.Text = "Stopping… (finishing saves in progress)";
        _cts?.Cancel();
    }

    private void SetRunning(bool running)
    {
        _busy = running;
        _stopping = false;
        _btnStart.Enabled = !running;
        _btnStop.Enabled = running;
        _txtPrompts.ReadOnly = running;
        _numConcurrency.Enabled = !running;
        _btnLoadCsv.Enabled = !running;
        _btnLoadPrompts.Enabled = !running;
    }

    /// <summary>End-of-run summary, shown in the status strip and the summary box — never a modal, so an unattended run is not held up.</summary>
    private void ReportFinished(BatchResult result)
    {
        var summary = $"{result.Completed} completed · {result.Skipped} skipped · {result.Failed} failed of {result.Total}";
        var reason = result.StopReason switch
        {
            BatchStopReason.AllAccountsQuarantined => "Stopped early: all accounts quarantined.",
            BatchStopReason.AccountsExhausted => "Stopped early: every account had its turn.",
            _ => null,
        };

        _lblStatus.Text = "Done — " + summary
            + (result.QuarantinedAccounts.Count > 0 ? $" · {result.QuarantinedAccounts.Count} account(s) quarantined" : string.Empty);

        var lines = new List<string>
        {
            $"Run finished {DateTime.Now:HH:mm:ss} — {summary} · {result.AccountsUsed} account(s) used.",
        };
        if (_resumedJobs.Count > 0) lines.Add($"Resume: {_resumedJobs.Count} image(s) were already done in an earlier run and were skipped.");
        if (reason is not null) lines.Add(reason);
        if (result.QuarantinedAccounts.Count > 0)
        {
            lines.Add("Skipped / quarantined accounts:");
            lines.AddRange(result.QuarantinedAccounts.Select(a => $"  {a.Email}: {a.Reason}"));
        }
        _txtRunSummary.Text = string.Join(Environment.NewLine, lines);
    }

    private IReadOnlyList<PromptJob> CurrentPrompts() => _csvPrompts ?? _promptSource.FromLines(_txtPrompts.Text);

    private void LoadCsv()
    {
        if (_busy) return;
        if (PromptForCsv() is { } path && LoadRoster(path) is not null)
            RefreshPromptPreview();
    }

    /// <summary>Loads a prompts CSV (<c>filename</c> + <c>prompt</c> columns, comma or '|' delimited) as the prompt source.</summary>
    private void LoadPromptsCsv()
    {
        if (_busy) return;

        using var dialog = new OpenFileDialog
        {
            Title = "Select prompts CSV (Filename | Prompt, or filename,prompt)",
            Filter = "CSV files (*.csv)|*.csv|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        IReadOnlyList<PromptJob> prompts;
        try
        {
            prompts = _promptSource.FromCsv(dialog.FileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load prompts CSV {Path}", dialog.FileName);
            _lblStatus.Text = "Prompts CSV not loaded: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Load prompts CSV", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (prompts.Count == 0)
        {
            ShowValidation($"{Path.GetFileName(dialog.FileName)} has no prompts.");
            return;
        }

        _csvPrompts = prompts;
        _promptCsvName = Path.GetFileName(dialog.FileName);
        _settingPromptsText = true;
        try
        {
            _txtPrompts.Text = string.Join(Environment.NewLine, prompts.Select(p => p.Prompt));
        }
        finally
        {
            _settingPromptsText = false;
        }
        RefreshPromptPreview();
    }

    /// <summary>Updates the prompt count and shows the jobs Start would run (planned per account once a roster is known).</summary>
    private void RefreshPromptPreview()
    {
        if (_busy) return;

        var prompts = CurrentPrompts();
        var count = prompts.Count == 0 ? "No prompts loaded"
            : _promptCsvName is not null ? $"{prompts.Count} prompt(s) loaded from {_promptCsvName}"
            : $"{prompts.Count} prompt(s) loaded";

        var jobs = _roster is not null ? _planner.Plan(prompts, _roster) : prompts;
        if (!ReferenceEquals(jobs, prompts))
            count += $" · {jobs.Count} image(s) planned across the accounts";

        _lblPromptCount.Text = count;
        PopulateGrid(jobs);
        _lblStatus.Text = jobs.Count == 0 ? "Idle" : $"Ready — {jobs.Count} image(s) to run";
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
            _roster = accounts;
            var enabled = accounts.Count(a => a.Enabled);
            var automated = accounts.Count(a => a.Credentials is not null);
            _lblAccounts.Text = $"{enabled} account(s) · {Path.GetFileName(csvPath)}";
            _lblStatus.Text = $"Loaded {accounts.Count} account(s) from {Path.GetFileName(csvPath)} ({automated} with credentials)";
            CapConcurrency(enabled);
            return accounts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load account CSV {Path}", csvPath);
            _lblStatus.Text = "CSV not loaded: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Load accounts CSV", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
    }

    /// <summary>More workers than accounts can never run, so the concurrency box tops out at the enabled-account count.</summary>
    private void CapConcurrency(int enabledAccounts)
    {
        var max = Math.Max(1, enabledAccounts);
        if (_numConcurrency.Value > max) _numConcurrency.Value = max;
        _numConcurrency.Maximum = max;
        _lblConcurrencyHint.Text = $"max {max} (enabled accounts)";
    }

    private bool TryGetOutputFolder(out string folder, out string error)
    {
        folder = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(_options.OutputFolder))
        {
            error = "Batch:OutputFolder is not set in appsettings.json.";
            return false;
        }

        try
        {
            folder = Path.GetFullPath(_options.OutputFolder);
            Directory.CreateDirectory(folder);
            return true;
        }
        catch (Exception ex)
        {
            error = $"{_options.OutputFolder}: {ex.Message}";
            return false;
        }
    }

    private void OpenOutputFolder()
    {
        if (!TryGetOutputFolder(out var folder, out var error))
        {
            _lblStatus.Text = "Output folder unavailable: " + error;
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open output folder {Folder}", folder);
            _lblStatus.Text = "Could not open output folder: " + ex.Message;
        }
    }

    private void ShowValidation(string message)
    {
        _lblStatus.Text = message;
        MessageBox.Show(this, message, "Cannot start", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void PopulateGrid(IReadOnlyList<PromptJob> jobs)
    {
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        _rowsByJob.Clear();
        _statusByJob.Clear();
        _resumedJobs.Clear();

        foreach (var job in jobs)
        {
            var index = _grid.Rows.Add(Preview(job.Prompt), job.DesiredFileName ?? "(auto)", job.Status.ToString(), string.Empty, "0", string.Empty);
            var row = _grid.Rows[index];
            row.Tag = job.Id;
            row.Cells["Prompt"].ToolTipText = job.Prompt;
            _rowsByJob[job.Id] = row;
            _statusByJob[job.Id] = job.Status;
        }

        _grid.ResumeLayout();
        _progress.Maximum = Math.Max(1, jobs.Count);
        _progress.Value = 0;
        _lblResume.Visible = false;
    }

    /// <summary>Updates only the affected row — no rebind, so the grid stays responsive under many updates.</summary>
    private void ApplyUpdate(JobUpdate update)
    {
        if (!_rowsByJob.TryGetValue(update.Id, out var row)) return;

        _statusByJob[update.Id] = update.Status;
        // The manifest skips already-finished jobs without an error; in-batch duplicates carry one.
        if (update.Status == JobStatus.Skipped && update.Error is null)
            _resumedJobs.Add(update.Id);

        if (update.SavedPath is not null)
            row.Cells["Filename"].Value = Path.GetFileName(update.SavedPath);

        row.Cells["Status"].Value = update.Status.ToString();
        row.Cells["Account"].Value = update.AccountId ?? string.Empty;
        row.Cells["Attempts"].Value = update.Attempts.ToString();
        row.Cells["Error"].Value = update.Error ?? string.Empty;
        row.Cells["Error"].ToolTipText = update.Error ?? string.Empty;

        var statusStyle = row.Cells["Status"].Style;
        statusStyle.BackColor = statusStyle.SelectionBackColor = StatusColor(update.Status);
        statusStyle.SelectionForeColor = Color.Black;

        RefreshSummary();
    }

    private void RefreshSummary()
    {
        var (completed, failed, skipped, running) = CountStatuses();
        var total = _statusByJob.Count;

        var text = $"{completed}/{total} complete · {failed} failed · {running} running";
        if (skipped > 0) text += $" · {skipped} skipped";
        _lblStatus.Text = _stopping ? "Stopping… " + text : text;
        _progress.Value = Math.Min(_progress.Maximum, completed + failed + skipped);

        _lblResume.Visible = _resumedJobs.Count > 0;
        _lblResume.Text = $"Resume detected: {_resumedJobs.Count} already done, skipping";
    }

    private (int Completed, int Failed, int Skipped, int Running) CountStatuses()
    {
        int completed = 0, failed = 0, skipped = 0, running = 0;
        foreach (var status in _statusByJob.Values)
        {
            switch (status)
            {
                case JobStatus.Completed: completed++; break;
                case JobStatus.Failed: failed++; break;
                case JobStatus.Skipped: skipped++; break;
                case JobStatus.Running or JobStatus.Downloading or JobStatus.Retrying: running++; break;
            }
        }
        return (completed, failed, skipped, running);
    }

    private static Color StatusColor(JobStatus status) => status switch
    {
        JobStatus.Completed => CompletedColor,
        JobStatus.Failed => FailedColor,
        JobStatus.Retrying => RetryingColor,
        JobStatus.Running or JobStatus.Downloading => RunningColor,
        JobStatus.Skipped => SkippedColor,
        _ => SystemColors.Window,
    };

    private static string Preview(string prompt) => prompt.Length <= 80 ? prompt : prompt[..80] + "…";
}
