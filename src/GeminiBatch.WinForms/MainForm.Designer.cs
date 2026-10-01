namespace GeminiBatch.WinForms;

partial class MainForm
{
    /// <summary>Required designer variable.</summary>
    private System.ComponentModel.IContainer components = null;

    private System.Windows.Forms.Label _lblPromptsHeader;
    private System.Windows.Forms.TextBox _txtPrompts;
    private System.Windows.Forms.Button _btnLoadPrompts;
    private System.Windows.Forms.Label _lblPromptCount;
    private System.Windows.Forms.CheckBox _chkUntilLimit;
    private System.Windows.Forms.Label _lblConcurrency;
    private System.Windows.Forms.NumericUpDown _numConcurrency;
    private System.Windows.Forms.Label _lblConcurrencyHint;
    private System.Windows.Forms.Button _btnStart;
    private System.Windows.Forms.Button _btnStop;
    private System.Windows.Forms.Button _btnLoadCsv;
    private System.Windows.Forms.Label _lblAccounts;
    private System.Windows.Forms.Button _btnOpenOutput;
    private System.Windows.Forms.TextBox _txtOutputFolder;
    private System.Windows.Forms.Button _btnBrowseOutput;
    private GeminiBatch.WinForms.BufferedDataGridView _grid;
    private System.Windows.Forms.DataGridViewTextBoxColumn _colPrompt;
    private System.Windows.Forms.DataGridViewTextBoxColumn _colSection;
    private System.Windows.Forms.DataGridViewTextBoxColumn _colFilename;
    private System.Windows.Forms.DataGridViewTextBoxColumn _colStatus;
    private System.Windows.Forms.DataGridViewTextBoxColumn _colAccount;
    private System.Windows.Forms.DataGridViewTextBoxColumn _colAttempts;
    private System.Windows.Forms.DataGridViewTextBoxColumn _colError;
    private System.Windows.Forms.TextBox _txtRunSummary;
    private System.Windows.Forms.StatusStrip _statusStrip;
    private System.Windows.Forms.ToolStripStatusLabel _lblStatus;
    private System.Windows.Forms.ToolStripStatusLabel _lblResume;
    private System.Windows.Forms.ToolStripProgressBar _progress;

    /// <summary>Clean up any resources being used.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    /// <summary>
    /// Required method for Designer support - do not modify
    /// the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        _lblPromptsHeader = new System.Windows.Forms.Label();
        _txtPrompts = new System.Windows.Forms.TextBox();
        _btnLoadPrompts = new System.Windows.Forms.Button();
        _lblPromptCount = new System.Windows.Forms.Label();
        _chkUntilLimit = new System.Windows.Forms.CheckBox();
        _lblConcurrency = new System.Windows.Forms.Label();
        _numConcurrency = new System.Windows.Forms.NumericUpDown();
        _lblConcurrencyHint = new System.Windows.Forms.Label();
        _btnStart = new System.Windows.Forms.Button();
        _btnStop = new System.Windows.Forms.Button();
        _btnLoadCsv = new System.Windows.Forms.Button();
        _lblAccounts = new System.Windows.Forms.Label();
        _btnOpenOutput = new System.Windows.Forms.Button();
        _txtOutputFolder = new System.Windows.Forms.TextBox();
        _btnBrowseOutput = new System.Windows.Forms.Button();
        _grid = new GeminiBatch.WinForms.BufferedDataGridView();
        _colPrompt = new System.Windows.Forms.DataGridViewTextBoxColumn();
        _colSection = new System.Windows.Forms.DataGridViewTextBoxColumn();
        _colFilename = new System.Windows.Forms.DataGridViewTextBoxColumn();
        _colStatus = new System.Windows.Forms.DataGridViewTextBoxColumn();
        _colAccount = new System.Windows.Forms.DataGridViewTextBoxColumn();
        _colAttempts = new System.Windows.Forms.DataGridViewTextBoxColumn();
        _colError = new System.Windows.Forms.DataGridViewTextBoxColumn();
        _txtRunSummary = new System.Windows.Forms.TextBox();
        _statusStrip = new System.Windows.Forms.StatusStrip();
        _lblStatus = new System.Windows.Forms.ToolStripStatusLabel();
        _lblResume = new System.Windows.Forms.ToolStripStatusLabel();
        _progress = new System.Windows.Forms.ToolStripProgressBar();
        ((System.ComponentModel.ISupportInitialize)_numConcurrency).BeginInit();
        ((System.ComponentModel.ISupportInitialize)_grid).BeginInit();
        _statusStrip.SuspendLayout();
        SuspendLayout();
        //
        // _lblPromptsHeader
        //
        _lblPromptsHeader.AutoSize = true;
        _lblPromptsHeader.Location = new System.Drawing.Point(12, 9);
        _lblPromptsHeader.Name = "_lblPromptsHeader";
        _lblPromptsHeader.Size = new System.Drawing.Size(240, 15);
        _lblPromptsHeader.TabIndex = 0;
        _lblPromptsHeader.Text = "Prompts (one per line, # for comments) — or load a prompts CSV:";
        //
        // _txtPrompts
        //
        _txtPrompts.AcceptsReturn = true;
        _txtPrompts.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
        _txtPrompts.Font = new System.Drawing.Font(System.Drawing.FontFamily.GenericMonospace, 9.5F);
        _txtPrompts.Location = new System.Drawing.Point(12, 30);
        _txtPrompts.Multiline = true;
        _txtPrompts.Name = "_txtPrompts";
        _txtPrompts.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
        _txtPrompts.Size = new System.Drawing.Size(976, 160);
        _txtPrompts.TabIndex = 1;
        _txtPrompts.TextChanged += OnPromptsTextChanged;
        //
        // _btnLoadPrompts
        //
        _btnLoadPrompts.Location = new System.Drawing.Point(12, 198);
        _btnLoadPrompts.Name = "_btnLoadPrompts";
        _btnLoadPrompts.Size = new System.Drawing.Size(150, 30);
        _btnLoadPrompts.TabIndex = 2;
        _btnLoadPrompts.Text = "Load prompts CSV…";
        _btnLoadPrompts.UseVisualStyleBackColor = true;
        _btnLoadPrompts.Click += OnLoadPromptsClick;
        //
        // _lblPromptCount
        //
        _lblPromptCount.AutoSize = true;
        _lblPromptCount.Location = new System.Drawing.Point(172, 206);
        _lblPromptCount.Name = "_lblPromptCount";
        _lblPromptCount.Size = new System.Drawing.Size(107, 15);
        _lblPromptCount.TabIndex = 3;
        _lblPromptCount.Text = "No prompts loaded";
        //
        // _chkUntilLimit
        //
        _chkUntilLimit.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
        _chkUntilLimit.Checked = true;
        _chkUntilLimit.CheckState = System.Windows.Forms.CheckState.Checked;
        _chkUntilLimit.Location = new System.Drawing.Point(668, 203);
        _chkUntilLimit.Name = "_chkUntilLimit";
        _chkUntilLimit.Size = new System.Drawing.Size(320, 21);
        _chkUntilLimit.TabIndex = 17;
        _chkUntilLimit.Text = "Generate until each account's daily limit";
        _chkUntilLimit.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        _chkUntilLimit.UseVisualStyleBackColor = true;
        _chkUntilLimit.CheckedChanged += OnUntilLimitChanged;
        //
        // _lblConcurrency
        //
        _lblConcurrency.AutoSize = true;
        _lblConcurrency.Location = new System.Drawing.Point(12, 247);
        _lblConcurrency.Name = "_lblConcurrency";
        _lblConcurrency.Size = new System.Drawing.Size(78, 15);
        _lblConcurrency.TabIndex = 4;
        _lblConcurrency.Text = "Concurrency:";
        //
        // _numConcurrency
        //
        _numConcurrency.Location = new System.Drawing.Point(96, 244);
        _numConcurrency.Maximum = new decimal(new int[] { 20, 0, 0, 0 });
        _numConcurrency.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
        _numConcurrency.Name = "_numConcurrency";
        _numConcurrency.Size = new System.Drawing.Size(60, 23);
        _numConcurrency.TabIndex = 5;
        _numConcurrency.Value = new decimal(new int[] { 1, 0, 0, 0 });
        //
        // _lblConcurrencyHint
        //
        _lblConcurrencyHint.AutoEllipsis = true;
        _lblConcurrencyHint.ForeColor = System.Drawing.SystemColors.GrayText;
        _lblConcurrencyHint.Location = new System.Drawing.Point(162, 247);
        _lblConcurrencyHint.Name = "_lblConcurrencyHint";
        _lblConcurrencyHint.Size = new System.Drawing.Size(170, 15);
        _lblConcurrencyHint.TabIndex = 6;
        _lblConcurrencyHint.Text = "cap set by accounts";
        //
        // _btnStart
        //
        _btnStart.Location = new System.Drawing.Point(340, 240);
        _btnStart.Name = "_btnStart";
        _btnStart.Size = new System.Drawing.Size(110, 30);
        _btnStart.TabIndex = 7;
        _btnStart.Text = "Start";
        _btnStart.UseVisualStyleBackColor = true;
        _btnStart.Click += OnStartClick;
        //
        // _btnStop
        //
        _btnStop.Enabled = false;
        _btnStop.Location = new System.Drawing.Point(456, 240);
        _btnStop.Name = "_btnStop";
        _btnStop.Size = new System.Drawing.Size(110, 30);
        _btnStop.TabIndex = 8;
        _btnStop.Text = "Stop";
        _btnStop.UseVisualStyleBackColor = true;
        _btnStop.Click += OnStopClick;
        //
        // _btnLoadCsv
        //
        _btnLoadCsv.Location = new System.Drawing.Point(584, 240);
        _btnLoadCsv.Name = "_btnLoadCsv";
        _btnLoadCsv.Size = new System.Drawing.Size(130, 30);
        _btnLoadCsv.TabIndex = 9;
        _btnLoadCsv.Text = "Accounts CSV…";
        _btnLoadCsv.UseVisualStyleBackColor = true;
        _btnLoadCsv.Click += OnLoadCsvClick;
        //
        // _lblAccounts
        //
        _lblAccounts.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
        _lblAccounts.AutoEllipsis = true;
        _lblAccounts.Location = new System.Drawing.Point(720, 247);
        _lblAccounts.Name = "_lblAccounts";
        _lblAccounts.Size = new System.Drawing.Size(268, 15);
        _lblAccounts.TabIndex = 10;
        _lblAccounts.Text = "No accounts loaded";
        //
        // _btnOpenOutput
        //
        _btnOpenOutput.Location = new System.Drawing.Point(12, 278);
        _btnOpenOutput.Name = "_btnOpenOutput";
        _btnOpenOutput.Size = new System.Drawing.Size(150, 30);
        _btnOpenOutput.TabIndex = 11;
        _btnOpenOutput.Text = "Open output folder";
        _btnOpenOutput.UseVisualStyleBackColor = true;
        _btnOpenOutput.Click += OnOpenOutputClick;
        //
        // _txtOutputFolder
        //
        _txtOutputFolder.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
        _txtOutputFolder.Location = new System.Drawing.Point(168, 282);
        _txtOutputFolder.Name = "_txtOutputFolder";
        _txtOutputFolder.ReadOnly = true;
        _txtOutputFolder.Size = new System.Drawing.Size(730, 23);
        _txtOutputFolder.TabIndex = 12;
        _txtOutputFolder.TabStop = false;
        //
        // _btnBrowseOutput
        //
        _btnBrowseOutput.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
        _btnBrowseOutput.Location = new System.Drawing.Point(904, 278);
        _btnBrowseOutput.Name = "_btnBrowseOutput";
        _btnBrowseOutput.Size = new System.Drawing.Size(84, 30);
        _btnBrowseOutput.TabIndex = 16;
        _btnBrowseOutput.Text = "Browse…";
        _btnBrowseOutput.UseVisualStyleBackColor = true;
        _btnBrowseOutput.Click += OnBrowseOutputClick;
        //
        // _grid
        //
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.BackgroundColor = System.Drawing.SystemColors.Window;
        _grid.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
        _grid.AutoSizeColumnsMode = System.Windows.Forms.DataGridViewAutoSizeColumnsMode.Fill;
        _grid.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] { _colPrompt, _colSection, _colFilename, _colStatus, _colAccount, _colAttempts, _colError });
        _grid.Location = new System.Drawing.Point(12, 316);
        _grid.MultiSelect = false;
        _grid.Name = "_grid";
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = System.Windows.Forms.DataGridViewSelectionMode.FullRowSelect;
        _grid.Size = new System.Drawing.Size(976, 192);
        _grid.TabIndex = 13;
        //
        // _colPrompt
        //
        _colPrompt.FillWeight = 26F;
        _colPrompt.HeaderText = "Prompt";
        _colPrompt.Name = "Prompt";
        _colPrompt.ReadOnly = true;
        //
        // _colSection
        //
        _colSection.FillWeight = 6F;
        _colSection.HeaderText = "Section";
        _colSection.Name = "Section";
        _colSection.ReadOnly = true;
        //
        // _colFilename
        //
        _colFilename.FillWeight = 18F;
        _colFilename.HeaderText = "Filename";
        _colFilename.Name = "Filename";
        _colFilename.ReadOnly = true;
        //
        // _colStatus
        //
        _colStatus.FillWeight = 10F;
        _colStatus.HeaderText = "Status";
        _colStatus.Name = "Status";
        _colStatus.ReadOnly = true;
        //
        // _colAccount
        //
        _colAccount.FillWeight = 14F;
        _colAccount.HeaderText = "Account";
        _colAccount.Name = "Account";
        _colAccount.ReadOnly = true;
        //
        // _colAttempts
        //
        _colAttempts.FillWeight = 7F;
        _colAttempts.HeaderText = "Attempts";
        _colAttempts.Name = "Attempts";
        _colAttempts.ReadOnly = true;
        //
        // _colError
        //
        _colError.FillWeight = 19F;
        _colError.HeaderText = "Error";
        _colError.Name = "Error";
        _colError.ReadOnly = true;
        //
        // _txtRunSummary
        //
        _txtRunSummary.Anchor = System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
        _txtRunSummary.Location = new System.Drawing.Point(12, 514);
        _txtRunSummary.Multiline = true;
        _txtRunSummary.Name = "_txtRunSummary";
        _txtRunSummary.ReadOnly = true;
        _txtRunSummary.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
        _txtRunSummary.Size = new System.Drawing.Size(976, 76);
        _txtRunSummary.TabIndex = 14;
        _txtRunSummary.Text = "Last run: none yet this session.";
        //
        // _statusStrip
        //
        _statusStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[] { _lblStatus, _lblResume, _progress });
        _statusStrip.Location = new System.Drawing.Point(0, 596);
        _statusStrip.Name = "_statusStrip";
        _statusStrip.Size = new System.Drawing.Size(1000, 24);
        _statusStrip.TabIndex = 15;
        //
        // _lblStatus
        //
        _lblStatus.Name = "_lblStatus";
        _lblStatus.Size = new System.Drawing.Size(560, 19);
        _lblStatus.Spring = true;
        _lblStatus.Text = "Idle";
        _lblStatus.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        //
        // _lblResume
        //
        _lblResume.ForeColor = System.Drawing.Color.DarkGreen;
        _lblResume.Name = "_lblResume";
        _lblResume.Size = new System.Drawing.Size(220, 19);
        _lblResume.Visible = false;
        //
        // _progress
        //
        _progress.Name = "_progress";
        _progress.Size = new System.Drawing.Size(200, 18);
        //
        // MainForm
        //
        AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
        ClientSize = new System.Drawing.Size(1000, 620);
        Controls.Add(_txtRunSummary);
        Controls.Add(_grid);
        Controls.Add(_btnBrowseOutput);
        Controls.Add(_txtOutputFolder);
        Controls.Add(_btnOpenOutput);
        Controls.Add(_lblAccounts);
        Controls.Add(_btnLoadCsv);
        Controls.Add(_btnStop);
        Controls.Add(_btnStart);
        Controls.Add(_lblConcurrencyHint);
        Controls.Add(_numConcurrency);
        Controls.Add(_lblConcurrency);
        Controls.Add(_chkUntilLimit);
        Controls.Add(_lblPromptCount);
        Controls.Add(_btnLoadPrompts);
        Controls.Add(_txtPrompts);
        Controls.Add(_lblPromptsHeader);
        Controls.Add(_statusStrip);
        MinimumSize = new System.Drawing.Size(1016, 600);
        Name = "MainForm";
        StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
        Text = "Gemini Batch Image Generator";
        FormClosed += OnFormClosed;
        ((System.ComponentModel.ISupportInitialize)_numConcurrency).EndInit();
        ((System.ComponentModel.ISupportInitialize)_grid).EndInit();
        _statusStrip.ResumeLayout(false);
        _statusStrip.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion
}
