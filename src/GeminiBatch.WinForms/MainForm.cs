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
    private readonly IPromptSource _promptSource = null!;
    private readonly ICsvAccountRoster _csvRoster = null!;
    private readonly OutputLocation _output = null!;
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
        IPromptSource promptSource,
        ICsvAccountRoster csvRoster,
        OutputLocation output,
        IOptions<BatchOptions> options,
        ILogger<MainForm> logger)
        : this()
    {
        _processor = processor;
        _promptSource = promptSource;
        _csvRoster = csvRoster;
        _output = output;
        _options = options.Value;
        _logger = logger;

        _numConcurrency.Value = Math.Clamp(_options.DefaultConcurrency, 1, (int)_numConcurrency.Maximum);

        // The folder picked with Browse… last time wins over Batch:OutputFolder.
        if (UiSettings.Load().OutputFolder is { Length: > 0 } saved)
        {
            try { _output.Root = saved; }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                _logger.LogWarning(ex, "Ignoring the saved output folder {Folder}", saved);
            }
        }
        ShowOutputFolder();
    }

    // ---- Event handlers (wired in the designer) ---------------------------------------------

    private async void OnStartClick(object? sender, EventArgs e) => await StartAsync();
    private void OnStopClick(object? sender, EventArgs e) => Stop();
    private void OnLoadCsvClick(object? sender, EventArgs e) => LoadCsv();
    private void OnLoadPromptsClick(object? sender, EventArgs e) => LoadPromptsCsv();
    private void OnOpenOutputClick(object? sender, EventArgs e) => OpenOutputFolder();
    private void OnBrowseOutputClick(object? sender, EventArgs e) => BrowseOutputFolder();
    private void OnFormClosed(object? sender, FormClosedEventArgs e) => _cts?.Cancel();

    private void OnPromptsTextChanged(object? sender, EventArgs e)
    {
        if (_settingPromptsText || _busy) return;
        // The operator typed or pasted: the text box is the prompt source again.
        _csvPrompts = null;
        _promptCsvName = null;
        RefreshPromptPreview(refillGrid: true);
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

        // One image per prompt. Fresh jobs every Start, so a re-run never reuses the last run's mutated jobs.
        var jobs = BuildJobs(prompts);
        PopulateGrid(jobs);
        SetRunning(true);
        var started = DateTime.Now;
        // Each worker runs its own browser and signs its account in (in parallel, up to Gemini:MaxConcurrentSignIns)
        // when its profile turns out to be signed out; an account generates until its daily limit, then the next one takes over.
        var concurrency = (int)_numConcurrency.Value;
        _txtRunSummary.Text = $"Run started {started:HH:mm:ss} — {jobs.Count} image(s), one per prompt, output: {outputFolder}";
        _lblStatus.Text = $"Running {jobs.Count} image(s) with {concurrency} browser(s) at a time…";
        _logger.LogInformation("Start clicked: {Jobs} job(s), concurrency {Concurrency}", jobs.Count, concurrency);

        // Progress<T> captures the UI SynchronizationContext here, so ApplyUpdate always runs on the UI thread.
        var progress = new Progress<JobUpdate>(ApplyUpdate);
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
            RefreshSummaryProgress();
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
        _btnBrowseOutput.Enabled = !running;
    }

    /// <summary>End-of-run summary, shown in the status strip and the summary box — never a modal, so an unattended run is not held up.</summary>
    private void ReportFinished(BatchResult result)
    {
        var summary = $"{result.Completed} completed · {result.Skipped} skipped · {result.Failed} failed of {result.Total}";
        var reason = result.StopReason switch
        {
            BatchStopReason.AllAccountsQuarantined => "Stopped early: all accounts quarantined.",
            BatchStopReason.AccountsExhausted => "Stopped early: every account reached its daily limit or was skipped before all images were "
                + "generated (see the Failed rows). Start again (tomorrow or with more accounts) to resume; finished images are skipped.",
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
        if (result.LimitReachedAccounts.Count > 0)
            lines.Add($"Daily limit reached: {result.LimitReachedAccounts.Count} account(s) — {string.Join(", ", result.LimitReachedAccounts)}");
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
            RefreshPromptPreview(refillGrid: false);
    }

    /// <summary>
    /// Loads a prompts CSV as the prompt source: <c>Image Prompt</c> + <c>Section</c> columns (each image is saved in its
    /// section's sub-folder), or the older <c>filename</c> + <c>prompt</c> layout; comma or '|' delimited.
    /// </summary>
    private void LoadPromptsCsv()
    {
        if (_busy) return;

        using var dialog = new OpenFileDialog
        {
            Title = "Select prompts CSV (Section + Image Prompt columns, or filename,prompt)",
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
        RefreshPromptPreview(refillGrid: true);
    }

    /// <summary>
    /// Updates the prompt count and the Ready text. With <paramref name="refillGrid"/> (the prompts changed) the grid
    /// shows one Pending row per prompt — the images Start will generate; switching the account CSV keeps the last
    /// run's rows.
    /// </summary>
    private void RefreshPromptPreview(bool refillGrid)
    {
        if (_busy) return;

        var prompts = CurrentPrompts();
        _lblPromptCount.Text = prompts.Count == 0 ? "No prompts loaded"
            : _promptCsvName is not null ? $"{prompts.Count} prompt(s) loaded from {_promptCsvName}"
            : $"{prompts.Count} prompt(s) loaded";

        var accounts = _roster is not null ? $" · {_roster.Count(a => a.Enabled)} account(s)" : string.Empty;
        _lblStatus.Text = prompts.Count == 0 ? "Idle"
            : $"Ready — {prompts.Count} image(s), one per prompt{accounts}; each account generates until its daily limit";

        if (refillGrid) PopulateGrid(BuildJobs(prompts));
    }

    /// <summary>One fresh, Pending job (image) per prompt.</summary>
    private static List<PromptJob> BuildJobs(IReadOnlyList<PromptJob> prompts) =>
        prompts.Select(p => new PromptJob { Prompt = p.Prompt, DesiredFileName = p.DesiredFileName, Section = p.Section }).ToList();

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
        folder = _output.Root;
        error = string.Empty;
        try
        {
            Directory.CreateDirectory(folder);
            return true;
        }
        catch (Exception ex)
        {
            error = $"{folder}: {ex.Message}";
            return false;
        }
    }

    private void ShowOutputFolder() => _txtOutputFolder.Text = _output.Root;

    /// <summary>Lets the operator pick the output folder (section sub-folders and the resume manifest go inside it) and remembers it.</summary>
    private void BrowseOutputFolder()
    {
        if (_busy) return;

        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the output folder (images are saved in a sub-folder per Section)",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(_output.Root) ? _output.Root : string.Empty,
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;

        _output.Root = dialog.SelectedPath;
        ShowOutputFolder();
        var settings = UiSettings.Load();
        settings.OutputFolder = _output.Root;
        _lblStatus.Text = settings.TrySave()
            ? $"Output folder: {_output.Root}"
            : $"Output folder: {_output.Root} (could not be remembered for next launch)";
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
            AddRow(job.Id, job.Prompt, job.DesiredFileName, job.Status);

        _grid.ResumeLayout();
        _progress.Maximum = Math.Max(1, jobs.Count);
        _progress.Value = 0;
        _lblResume.Visible = false;
    }

    /// <summary>
    /// One row per image (= per prompt). The prompt is too long for a column: it is the Filename cell's tooltip, and the
    /// saved name (shown relative to the output folder) carries the section folder.
    /// </summary>
    private DataGridViewRow AddRow(Guid id, string prompt, string? desiredFileName, JobStatus status)
    {
        var index = _grid.Rows.Add(desiredFileName ?? "(auto)", status.ToString(), string.Empty, "0", string.Empty, string.Empty);
        var row = _grid.Rows[index];
        row.Tag = id;
        row.Cells["Filename"].ToolTipText = "Prompt: " + prompt;
        _rowsByJob[id] = row;
        _statusByJob[id] = status;
        return row;
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
        {
            // Relative to the output folder, so the section sub-folder shows (e.g. A\red_mug.jpg).
            var filenameCell = row.Cells["Filename"];
            filenameCell.Value = Path.GetRelativePath(_output.Root, update.SavedPath);
            if (!filenameCell.ToolTipText.Contains(update.SavedPath, StringComparison.OrdinalIgnoreCase))
                filenameCell.ToolTipText += Environment.NewLine + "Saved: " + update.SavedPath;
        }

        if (update.Status == JobStatus.Running && row.Cells["Started"].Value is null or "")
            row.Cells["Started"].Value = DateTime.Now.ToString("HH:mm:ss");

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
        RefreshSummaryProgress();

        _lblResume.Visible = _resumedJobs.Count > 0;
        _lblResume.Text = $"Resume detected: {_resumedJobs.Count} already done, skipping";
    }

    /// <summary>The bar shows done / total.</summary>
    private void RefreshSummaryProgress()
    {
        var (completed, failed, skipped, _) = CountStatuses();
        _progress.Maximum = Math.Max(1, _statusByJob.Count);
        _progress.Value = Math.Min(_progress.Maximum, completed + failed + skipped);
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
}
