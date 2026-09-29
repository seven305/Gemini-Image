namespace GeminiBatch.WinForms;

partial class MainForm
{
    /// <summary>Required designer variable.</summary>
    private System.ComponentModel.IContainer components = null;

    private System.Windows.Forms.Label _lblPromptsHeader;
    private System.Windows.Forms.TextBox _txtPrompts;
    private System.Windows.Forms.Label _lblConcurrency;
    private System.Windows.Forms.NumericUpDown _numConcurrency;
    private System.Windows.Forms.Button _btnStart;
    private System.Windows.Forms.Button _btnStop;
    private System.Windows.Forms.Button _btnLoadCsv;
    private System.Windows.Forms.Label _lblStatus;
    private System.Windows.Forms.DataGridView _grid;
    private System.Windows.Forms.DataGridViewTextBoxColumn _colPrompt;
    private System.Windows.Forms.DataGridViewTextBoxColumn _colFilename;
    private System.Windows.Forms.DataGridViewTextBoxColumn _colStatus;
    private System.Windows.Forms.DataGridViewTextBoxColumn _colAccount;
    private System.Windows.Forms.DataGridViewTextBoxColumn _colAttempts;

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
        _lblConcurrency = new System.Windows.Forms.Label();
        _numConcurrency = new System.Windows.Forms.NumericUpDown();
        _btnStart = new System.Windows.Forms.Button();
        _btnStop = new System.Windows.Forms.Button();
        _btnLoadCsv = new System.Windows.Forms.Button();
        _lblStatus = new System.Windows.Forms.Label();
        _grid = new System.Windows.Forms.DataGridView();
        _colPrompt = new System.Windows.Forms.DataGridViewTextBoxColumn();
        _colFilename = new System.Windows.Forms.DataGridViewTextBoxColumn();
        _colStatus = new System.Windows.Forms.DataGridViewTextBoxColumn();
        _colAccount = new System.Windows.Forms.DataGridViewTextBoxColumn();
        _colAttempts = new System.Windows.Forms.DataGridViewTextBoxColumn();
        ((System.ComponentModel.ISupportInitialize)_numConcurrency).BeginInit();
        ((System.ComponentModel.ISupportInitialize)_grid).BeginInit();
        SuspendLayout();
        //
        // _lblPromptsHeader
        //
        _lblPromptsHeader.AutoSize = true;
        _lblPromptsHeader.Location = new System.Drawing.Point(12, 9);
        _lblPromptsHeader.Name = "_lblPromptsHeader";
        _lblPromptsHeader.Size = new System.Drawing.Size(240, 15);
        _lblPromptsHeader.TabIndex = 0;
        _lblPromptsHeader.Text = "Prompts (one per line, # for comments):";
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
        _txtPrompts.Size = new System.Drawing.Size(860, 200);
        _txtPrompts.TabIndex = 1;
        //
        // _lblConcurrency
        //
        _lblConcurrency.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left;
        _lblConcurrency.AutoSize = true;
        _lblConcurrency.Location = new System.Drawing.Point(12, 244);
        _lblConcurrency.Name = "_lblConcurrency";
        _lblConcurrency.Size = new System.Drawing.Size(78, 15);
        _lblConcurrency.TabIndex = 2;
        _lblConcurrency.Text = "Concurrency:";
        //
        // _numConcurrency
        //
        _numConcurrency.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left;
        _numConcurrency.Location = new System.Drawing.Point(96, 242);
        _numConcurrency.Maximum = new decimal(new int[] { 20, 0, 0, 0 });
        _numConcurrency.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
        _numConcurrency.Name = "_numConcurrency";
        _numConcurrency.Size = new System.Drawing.Size(60, 23);
        _numConcurrency.TabIndex = 3;
        _numConcurrency.Value = new decimal(new int[] { 1, 0, 0, 0 });
        //
        // _btnStart
        //
        _btnStart.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left;
        _btnStart.Location = new System.Drawing.Point(172, 241);
        _btnStart.Name = "_btnStart";
        _btnStart.Size = new System.Drawing.Size(90, 25);
        _btnStart.TabIndex = 4;
        _btnStart.Text = "Start";
        _btnStart.UseVisualStyleBackColor = true;
        _btnStart.Click += OnStartClick;
        //
        // _btnStop
        //
        _btnStop.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left;
        _btnStop.Enabled = false;
        _btnStop.Location = new System.Drawing.Point(268, 241);
        _btnStop.Name = "_btnStop";
        _btnStop.Size = new System.Drawing.Size(90, 25);
        _btnStop.TabIndex = 5;
        _btnStop.Text = "Stop";
        _btnStop.UseVisualStyleBackColor = true;
        _btnStop.Click += OnStopClick;
        //
        // _btnLoadCsv
        //
        _btnLoadCsv.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left;
        _btnLoadCsv.Location = new System.Drawing.Point(380, 241);
        _btnLoadCsv.Name = "_btnLoadCsv";
        _btnLoadCsv.Size = new System.Drawing.Size(90, 25);
        _btnLoadCsv.TabIndex = 6;
        _btnLoadCsv.Text = "Load CSV…";
        _btnLoadCsv.UseVisualStyleBackColor = true;
        _btnLoadCsv.Click += OnLoadCsvClick;
        //
        // _lblStatus
        //
        _lblStatus.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
        _lblStatus.AutoEllipsis = true;
        _lblStatus.Location = new System.Drawing.Point(12, 272);
        _lblStatus.Name = "_lblStatus";
        _lblStatus.Size = new System.Drawing.Size(860, 20);
        _lblStatus.TabIndex = 7;
        _lblStatus.Text = "Idle";
        //
        // _grid
        //
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
        _grid.AutoSizeColumnsMode = System.Windows.Forms.DataGridViewAutoSizeColumnsMode.Fill;
        _grid.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] { _colPrompt, _colFilename, _colStatus, _colAccount, _colAttempts });
        _grid.Location = new System.Drawing.Point(12, 298);
        _grid.Name = "_grid";
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = System.Windows.Forms.DataGridViewSelectionMode.FullRowSelect;
        _grid.Size = new System.Drawing.Size(860, 292);
        _grid.TabIndex = 8;
        //
        // _colPrompt
        //
        _colPrompt.FillWeight = 40F;
        _colPrompt.HeaderText = "Prompt";
        _colPrompt.Name = "Prompt";
        _colPrompt.ReadOnly = true;
        //
        // _colFilename
        //
        _colFilename.FillWeight = 25F;
        _colFilename.HeaderText = "Filename";
        _colFilename.Name = "Filename";
        _colFilename.ReadOnly = true;
        //
        // _colStatus
        //
        _colStatus.FillWeight = 12F;
        _colStatus.HeaderText = "Status";
        _colStatus.Name = "Status";
        _colStatus.ReadOnly = true;
        //
        // _colAccount
        //
        _colAccount.FillWeight = 13F;
        _colAccount.HeaderText = "Account";
        _colAccount.Name = "Account";
        _colAccount.ReadOnly = true;
        //
        // _colAttempts
        //
        _colAttempts.FillWeight = 10F;
        _colAttempts.HeaderText = "Attempts";
        _colAttempts.Name = "Attempts";
        _colAttempts.ReadOnly = true;
        //
        // MainForm
        //
        AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
        ClientSize = new System.Drawing.Size(884, 602);
        Controls.Add(_grid);
        Controls.Add(_lblStatus);
        Controls.Add(_btnLoadCsv);
        Controls.Add(_btnStop);
        Controls.Add(_btnStart);
        Controls.Add(_numConcurrency);
        Controls.Add(_lblConcurrency);
        Controls.Add(_txtPrompts);
        Controls.Add(_lblPromptsHeader);
        MinimumSize = new System.Drawing.Size(900, 600);
        Name = "MainForm";
        StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
        Text = "Gemini Batch Image Generator";
        FormClosed += OnFormClosed;
        ((System.ComponentModel.ISupportInitialize)_numConcurrency).EndInit();
        ((System.ComponentModel.ISupportInitialize)_grid).EndInit();
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion
}
