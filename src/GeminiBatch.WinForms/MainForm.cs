using GeminiBatch.Application;
using GeminiBatch.Application.Abstractions;
using GeminiBatch.Application.Models;
using GeminiBatch.Application.Options;
using GeminiBatch.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeminiBatch.WinForms;

public sealed class MainForm : Form
{
    private readonly BatchProcessor _processor;
    private readonly IPromptSource _promptSource;
    private readonly IAccountStore _accountStore;
    private readonly BatchOptions _options;
    private readonly ILogger<MainForm> _logger;

    private readonly TextBox _txtPrompts = new();
    private readonly NumericUpDown _numConcurrency = new();
    private readonly Button _btnStart = new();
    private readonly Button _btnStop = new();
    private readonly Label _lblStatus = new();
    private readonly DataGridView _grid = new();

    private readonly Dictionary<Guid, DataGridViewRow> _rowsByJob = new();
    private CancellationTokenSource? _cts;

    public MainForm(
        BatchProcessor processor,
        IPromptSource promptSource,
        IAccountStore accountStore,
        IOptions<BatchOptions> options,
        ILogger<MainForm> logger)
    {
        _processor = processor;
        _promptSource = promptSource;
        _accountStore = accountStore;
        _options = options.Value;
        _logger = logger;

        BuildLayout();
    }

    private void BuildLayout()
    {
        Text = "Gemini Batch Image Generator";
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 65));

        root.Controls.Add(new Label { Text = "Prompts (one per line, # for comments):", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);

        _txtPrompts.Multiline = true;
        _txtPrompts.ScrollBars = ScrollBars.Vertical;
        _txtPrompts.AcceptsReturn = true;
        _txtPrompts.Dock = DockStyle.Fill;
        _txtPrompts.Font = new Font(FontFamily.GenericMonospace, 9.5f);
        root.Controls.Add(_txtPrompts, 0, 1);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        toolbar.Controls.Add(new Label { Text = "Concurrency:", AutoSize = true, Margin = new Padding(0, 8, 4, 0) });
        _numConcurrency.Minimum = 1;
        _numConcurrency.Maximum = 20;
        _numConcurrency.Value = Math.Clamp(_options.DefaultConcurrency, 1, 20);
        _numConcurrency.Width = 60;
        _numConcurrency.Margin = new Padding(0, 4, 12, 0);
        toolbar.Controls.Add(_numConcurrency);

        _btnStart.Text = "Start";
        _btnStart.Width = 90;
        _btnStart.Click += async (_, _) => await StartAsync();
        toolbar.Controls.Add(_btnStart);

        _btnStop.Text = "Stop";
        _btnStop.Width = 90;
        _btnStop.Enabled = false;
        _btnStop.Click += (_, _) => Stop();
        toolbar.Controls.Add(_btnStop);

        _lblStatus.AutoSize = true;
        _lblStatus.Margin = new Padding(16, 8, 0, 0);
        _lblStatus.Text = "Idle";
        toolbar.Controls.Add(_lblStatus);
        root.Controls.Add(toolbar, 0, 2);

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Prompt", HeaderText = "Prompt", FillWeight = 40 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Filename", HeaderText = "Filename", FillWeight = 25 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", FillWeight = 12 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Account", HeaderText = "Account", FillWeight = 13 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Attempts", HeaderText = "Attempts", FillWeight = 10 });
        root.Controls.Add(_grid, 0, 3);

        Controls.Add(root);
    }

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
            accounts = await _accountStore.LoadAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load accounts");
            MessageBox.Show(this, ex.Message, "Accounts", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

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
        _btnStart.Enabled = !running;
        _btnStop.Enabled = running;
        _txtPrompts.ReadOnly = running;
        _numConcurrency.Enabled = !running;
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
        }
        base.Dispose(disposing);
    }
}
