using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NasPosSetup;

/// <summary>
/// Setup wizard: Welcome -> SQL Server (pick a detected instance or install SQL Express) -> database / IIS settings -> progress -> finish.
/// The actual work is done by install.ps1 -Gui; this form passes the choices in and turns its ##PROGRESS/##ERROR lines into UI.
/// </summary>
internal sealed class SetupForm : Form
{
    private const string Title = "NAS POS Pro Setup";
    private const int PageWelcome = 0, PageSql = 1, PageApp = 2, PageBackup = 3, PageProgress = 4, PageFinish = 5;
    private static readonly Regex IdentifierRx = new Regex(@"^[A-Za-z_][A-Za-z0-9_]{0,99}$");

    /// <summary>Interval choices: text shown, value passed to install.ps1 -BackupInterval.</summary>
    private static readonly (string Text, string Value)[] Intervals =
    {
        ("Every hour", "1h"), ("Every 2 hours", "2h"), ("Every 4 hours", "4h"), ("Every 6 hours", "6h"), ("Every 12 hours", "12h"),
        ("Once a day", "Daily"), ("Once a week", "Weekly"),
    };
    private static readonly (string Text, string Value)[] Days =
    {
        ("Sunday", "SUN"), ("Monday", "MON"), ("Tuesday", "TUE"), ("Wednesday", "WED"), ("Thursday", "THU"), ("Friday", "FRI"), ("Saturday", "SAT"),
    };

    public int ExitCode { get; private set; } = 1;

    private readonly Label _headTitle = new Label(), _headText = new Label();
    private readonly Panel _pWelcome = new Panel(), _pSql = new Panel(), _pApp = new Panel(), _pBackup = new Panel(), _pProgress = new Panel(), _pFinish = new Panel();
    private readonly Button _btnBack = new Button(), _btnNext = new Button(), _btnCancel = new Button();

    // SQL page
    private readonly ListView _lvInstances = new ListView();
    private readonly TextBox _txtServer = new TextBox();
    private readonly RadioButton _rbExisting = new RadioButton(), _rbInstallNew = new RadioButton();
    private readonly GroupBox _gbAuth = new GroupBox();
    private readonly RadioButton _rbWinAuth = new RadioButton(), _rbSqlAuth = new RadioButton();
    private readonly TextBox _txtAdminUser = new TextBox(), _txtAdminPwd = new TextBox();
    private readonly Button _btnTest = new Button();
    private readonly Label _lblTest = new Label();
    private string _testedKey;

    // App page
    private readonly Label _lblServerValue = new Label(), _lblUrl = new Label();
    private readonly TextBox _txtDb = new TextBox(), _txtLogin = new TextBox(), _txtPwd = new TextBox(), _txtDir = new TextBox(), _txtPort = new TextBox();
    private readonly CheckBox _chkShowPwd = new CheckBox();

    // Backup page
    private readonly CheckBox _chkBackup = new CheckBox();
    private readonly GroupBox _gbBackupDir = new GroupBox(), _gbSchedule = new GroupBox();
    private readonly TextBox _txtBackupDir = new TextBox();
    private readonly ComboBox _cmbInterval = new ComboBox(), _cmbDay = new ComboBox();
    private readonly DateTimePicker _dtpTime = new DateTimePicker();
    private readonly NumericUpDown _numKeep = new NumericUpDown();
    private readonly Label _lblTimeCaption = new Label(), _lblDayCaption = new Label(), _lblBackupSummary = new Label();

    // Progress / finish pages
    private readonly Label _lblStep = new Label();
    private readonly ProgressBar _progress = new ProgressBar();
    private readonly TextBox _txtLog = new TextBox();
    private readonly Label _lblResult = new Label(), _lblDetails = new Label();
    private readonly CheckBox _chkOpen = new CheckBox();
    private readonly LinkLabel _lnkLog = new LinkLabel();

    private List<SqlInstanceInfo> _instances = new List<SqlInstanceInfo>();
    private Panel[] _pages;
    private int _page;
    private bool _running, _done, _restartNeeded;
    private string _logPath, _appUrl, _error;
    private readonly List<string> _warnings = new List<string>();

    public SetupForm()
    {
        SuspendLayout();
        Text = Title;
        Font = new Font("Segoe UI", 9F);
        // Layout is in 96-DPI pixels and scaled once by ScaleForDpi(). AutoScaleMode.Dpi is not used: on .NET Framework it scales
        // fixed-size Labels twice (2.25x at 150%), which made the backup time box disappear under its caption.
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(640, 470);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* default icon */ }

        var header = new Panel { Location = new Point(0, 0), Size = new Size(640, 60), BackColor = Color.White };
        _headTitle.SetBounds(20, 10, 600, 22);
        _headTitle.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
        _headText.SetBounds(22, 34, 600, 20);
        header.Controls.AddRange(new Control[] { _headTitle, _headText });
        Controls.Add(header);
        Controls.Add(new Label { BorderStyle = BorderStyle.Fixed3D, Location = new Point(0, 60), Size = new Size(640, 2) });
        Controls.Add(new Label { BorderStyle = BorderStyle.Fixed3D, Location = new Point(0, 418), Size = new Size(640, 2) });

        _btnBack.SetBounds(340, 430, 90, 28); _btnBack.Text = "< Back"; _btnBack.Click += (s, e) => ShowPage(_page - 1);
        _btnNext.SetBounds(436, 430, 90, 28); _btnNext.Text = "Next >"; _btnNext.Click += async (s, e) => await NextAsync();
        _btnCancel.SetBounds(536, 430, 90, 28); _btnCancel.Text = "Cancel"; _btnCancel.Click += (s, e) => Close();
        Controls.AddRange(new Control[] { _btnBack, _btnNext, _btnCancel });
        AcceptButton = _btnNext;

        _pages = new[] { _pWelcome, _pSql, _pApp, _pBackup, _pProgress, _pFinish };
        foreach (var p in _pages)
        {
            p.SetBounds(0, 62, 640, 354);
            p.Visible = false;
            Controls.Add(p);
        }
        BuildWelcome();
        BuildSql();
        BuildApp();
        BuildBackup();
        BuildProgress();
        BuildFinish();
        ResumeLayout(false);
        ScaleForDpi();

        LoadPreviousInstall();
        RefreshInstances();
        UpdateBackupPage();
        ShowPage(PageWelcome);
    }

    /// <summary>Scales the 96-DPI layout to the screen DPI (the exe is system-DPI-aware, so fonts already render at screen DPI).</summary>
    private void ScaleForDpi()
    {
        float dpi;
        using (var g = CreateGraphics()) dpi = g.DpiX;
        if (Math.Abs(dpi - 96F) < 0.5F) return;
        float k = dpi / 96F;
        Scale(new SizeF(k, k));
        foreach (ColumnHeader c in _lvInstances.Columns) c.Width = (int)Math.Round(c.Width * k);   // Scale() leaves column widths alone
    }

    // ------------------------------------------------------------------ pages

    private void BuildWelcome()
    {
        var b = "\u2022  ";
        _pWelcome.Controls.Add(new Label
        {
            Location = new Point(24, 16),
            Size = new Size(592, 330),
            Text = "This wizard installs NAS POS Pro on this computer and hosts it in IIS.\r\n\r\n" +
                   "Setup will:\r\n" +
                   b + "Turn on IIS and install the .NET 9 Hosting Bundle (if missing)\r\n" +
                   b + "Use a SQL Server already on this computer, or install SQL Server 2022 Express\r\n" +
                   b + "Create the SQL login NAS and the database NASDB with all tables\r\n" +
                   b + "Schedule automatic database backups to a folder you choose\r\n" +
                   b + "Host the application in IIS on http://localhost:4300 and allow other\r\n" +
                   "    computers on the local network to open it\r\n\r\n" +
                   "An internet connection is needed only if SQL Server Express or the Hosting Bundle\r\n" +
                   "has to be downloaded.\r\n\r\n" +
                   "If NAS POS Pro is already installed it is upgraded and your data is kept.\r\n\r\n" +
                   "Click Next to continue."
        });
    }

    private void BuildSql()
    {
        _pSql.Controls.Add(new Label { Text = "SQL Server instances found on this computer:", Location = new Point(16, 10), AutoSize = true });

        _lvInstances.SetBounds(16, 30, 608, 108);
        _lvInstances.View = View.Details;
        _lvInstances.FullRowSelect = true;
        _lvInstances.HideSelection = false;
        _lvInstances.MultiSelect = false;
        _lvInstances.Columns.Add("Instance", 130);
        _lvInstances.Columns.Add("Server name", 170);
        _lvInstances.Columns.Add("Version", 150);
        _lvInstances.Columns.Add("Status", 80);
        _lvInstances.SelectedIndexChanged += (s, e) =>
        {
            if (_lvInstances.SelectedItems.Count == 1) _txtServer.Text = ((SqlInstanceInfo)_lvInstances.SelectedItems[0].Tag).Server;
        };
        _pSql.Controls.Add(_lvInstances);

        var btnRefresh = new Button { Text = "Refresh", Location = new Point(534, 144), Size = new Size(90, 25) };
        btnRefresh.Click += (s, e) => RefreshInstances();
        _pSql.Controls.Add(btnRefresh);

        _rbExisting.Text = "Use this SQL Server:";
        _rbExisting.SetBounds(16, 146, 150, 22);
        _txtServer.SetBounds(170, 145, 250, 23);
        _txtServer.TextChanged += (s, e) => ClearTest();
        _rbInstallNew.Text = "Install a new SQL Server 2022 Express instance (free)";
        _rbInstallNew.SetBounds(16, 174, 500, 22);
        _rbExisting.CheckedChanged += (s, e) => UpdateSqlPage();
        _pSql.Controls.AddRange(new Control[] { _rbExisting, _txtServer, _rbInstallNew });

        _gbAuth.Text = "Administrator sign-in used by setup (must be sysadmin)";
        _gbAuth.SetBounds(16, 204, 608, 140);
        _rbWinAuth.Text = "Windows authentication (" + Environment.UserDomainName + "\\" + Environment.UserName + ")";
        _rbWinAuth.SetBounds(14, 22, 440, 22);
        _rbWinAuth.Checked = true;
        _rbSqlAuth.Text = "SQL Server login:";
        _rbSqlAuth.SetBounds(14, 48, 130, 22);
        _txtAdminUser.SetBounds(150, 47, 140, 23);
        _txtAdminUser.Text = "sa";
        var lblPwd = new Label { Text = "Password:", Location = new Point(300, 51), AutoSize = true };
        _txtAdminPwd.SetBounds(370, 47, 150, 23);
        _txtAdminPwd.UseSystemPasswordChar = true;
        _rbSqlAuth.CheckedChanged += (s, e) => { UpdateSqlPage(); ClearTest(); };
        _txtAdminUser.TextChanged += (s, e) => ClearTest();
        _txtAdminPwd.TextChanged += (s, e) => ClearTest();
        _btnTest.Text = "Test connection";
        _btnTest.SetBounds(14, 80, 120, 27);
        _btnTest.Click += async (s, e) => await TestConnectionAsync();
        _lblTest.SetBounds(142, 78, 456, 56);
        _gbAuth.Controls.AddRange(new Control[] { _rbWinAuth, _rbSqlAuth, _txtAdminUser, lblPwd, _txtAdminPwd, _btnTest, _lblTest });
        _pSql.Controls.Add(_gbAuth);
    }

    private void BuildApp()
    {
        var gbDb = new GroupBox { Text = "Database", Location = new Point(16, 8), Size = new Size(608, 150) };
        gbDb.Controls.Add(new Label { Text = "SQL Server:", Location = new Point(16, 28), AutoSize = true });
        _lblServerValue.SetBounds(140, 28, 450, 20);
        _lblServerValue.Font = new Font(Font, FontStyle.Bold);
        gbDb.Controls.Add(new Label { Text = "Database name:", Location = new Point(16, 57), AutoSize = true });
        _txtDb.SetBounds(140, 53, 200, 23);
        _txtDb.Text = "NASDB";
        gbDb.Controls.Add(new Label { Text = "SQL login (user):", Location = new Point(16, 87), AutoSize = true });
        _txtLogin.SetBounds(140, 83, 200, 23);
        _txtLogin.Text = "NAS";
        gbDb.Controls.Add(new Label { Text = "Password:", Location = new Point(16, 117), AutoSize = true });
        _txtPwd.SetBounds(140, 113, 200, 23);
        _txtPwd.Text = "admin@123";
        _txtPwd.UseSystemPasswordChar = true;
        _chkShowPwd.Text = "Show";
        _chkShowPwd.SetBounds(350, 115, 80, 22);
        _chkShowPwd.CheckedChanged += (s, e) => _txtPwd.UseSystemPasswordChar = !_chkShowPwd.Checked;
        gbDb.Controls.AddRange(new Control[] { _lblServerValue, _txtDb, _txtLogin, _txtPwd, _chkShowPwd });
        _pApp.Controls.Add(gbDb);

        var gbWeb = new GroupBox { Text = "Web site (IIS)", Location = new Point(16, 166), Size = new Size(608, 96) };
        gbWeb.Controls.Add(new Label { Text = "Install folder:", Location = new Point(16, 29), AutoSize = true });
        _txtDir.SetBounds(140, 25, 360, 23);
        _txtDir.Text = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), @"inetpub\NasPosPro");
        var btnBrowse = new Button { Text = "Browse...", Location = new Point(506, 24), Size = new Size(86, 25) };
        btnBrowse.Click += (s, e) =>
        {
            using (var dlg = new FolderBrowserDialog { SelectedPath = _txtDir.Text, Description = "Folder for the NAS POS Pro web files" })
                if (dlg.ShowDialog(this) == DialogResult.OK) _txtDir.Text = Path.Combine(dlg.SelectedPath, "NasPosPro");
        };
        gbWeb.Controls.Add(new Label { Text = "Port:", Location = new Point(16, 61), AutoSize = true });
        _txtPort.SetBounds(140, 57, 80, 23);
        _txtPort.Text = "4300";
        _txtPort.TextChanged += (s, e) => _lblUrl.Text = "http://localhost:" + _txtPort.Text.Trim() + "/";
        _lblUrl.SetBounds(232, 61, 300, 20);
        _lblUrl.Text = "http://localhost:4300/";
        gbWeb.Controls.AddRange(new Control[] { _txtDir, btnBrowse, _txtPort, _lblUrl });
        _pApp.Controls.Add(gbWeb);

        _pApp.Controls.Add(new Label
        {
            Location = new Point(18, 272),
            Size = new Size(604, 76),
            ForeColor = SystemColors.GrayText,
            Text = "Setup creates the login (or resets its password if it already exists) and makes it the owner of this " +
                   "database only - it gets no server-wide rights. The application connects with this login. If the SQL Server " +
                   "accepts Windows logins only, SQL Server authentication is turned on and the SQL service restarts once."
        });
    }

    private void BuildBackup()
    {
        _chkBackup.Text = "Back up the database automatically (recommended)";
        _chkBackup.SetBounds(16, 8, 560, 24);
        _chkBackup.Checked = true;
        _chkBackup.CheckedChanged += (s, e) => UpdateBackupPage();
        _pBackup.Controls.Add(_chkBackup);

        _gbBackupDir.Text = "Backup folder";
        _gbBackupDir.SetBounds(16, 36, 608, 86);
        _txtBackupDir.SetBounds(16, 24, 476, 23);
        _txtBackupDir.Text = DefaultBackupDir();
        var btnBrowse = new Button { Text = "Browse...", Location = new Point(498, 23), Size = new Size(94, 25) };
        btnBrowse.Click += (s, e) =>
        {
            using (var dlg = new FolderBrowserDialog { SelectedPath = _txtBackupDir.Text, Description = "Folder for the database backups", ShowNewFolderButton = true })
                if (dlg.ShowDialog(this) == DialogResult.OK) _txtBackupDir.Text = dlg.SelectedPath;
        };
        _gbBackupDir.Controls.AddRange(new Control[]
        {
            _txtBackupDir, btnBrowse,
            new Label
            {
                Location = new Point(16, 52), Size = new Size(580, 30), ForeColor = SystemColors.GrayText,
                Text = "Prefer another disk, a USB drive or a network folder (\\\\server\\share) - backups on the same disk as Windows are lost if that disk fails.",
            },
        });
        _pBackup.Controls.Add(_gbBackupDir);

        _gbSchedule.Text = "Schedule";
        _gbSchedule.SetBounds(16, 130, 608, 122);
        _gbSchedule.Controls.Add(new Label { Text = "Back up:", Location = new Point(16, 28), AutoSize = true });
        _cmbInterval.SetBounds(130, 24, 150, 23);
        _cmbInterval.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbInterval.Items.AddRange(Intervals.Select(i => (object)i.Text).ToArray());
        _cmbInterval.SelectedIndex = Array.FindIndex(Intervals, i => i.Value == "Daily");
        _cmbInterval.SelectedIndexChanged += (s, e) => UpdateBackupPage();
        _lblDayCaption.Text = "On:";
        _lblDayCaption.SetBounds(300, 28, 30, 20);
        _cmbDay.SetBounds(336, 24, 130, 23);
        _cmbDay.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbDay.Items.AddRange(Days.Select(d => (object)d.Text).ToArray());
        _cmbDay.SelectedIndex = 0;
        _cmbDay.SelectedIndexChanged += (s, e) => UpdateBackupPage();
        _lblTimeCaption.SetBounds(16, 60, 110, 20);
        _dtpTime.SetBounds(130, 56, 90, 23);
        _dtpTime.Format = DateTimePickerFormat.Custom;
        _dtpTime.CustomFormat = "HH:mm";
        _dtpTime.ShowUpDown = true;
        _dtpTime.Value = DateTime.Today.AddHours(21);
        _dtpTime.ValueChanged += (s, e) => UpdateBackupPage();
        _gbSchedule.Controls.Add(new Label { Text = "Keep backups for:", Location = new Point(16, 92), AutoSize = true });
        _numKeep.SetBounds(130, 88, 70, 23);
        _numKeep.Minimum = 1;
        _numKeep.Maximum = 3650;
        _numKeep.Value = 30;
        _numKeep.ValueChanged += (s, e) => UpdateBackupPage();
        _gbSchedule.Controls.Add(new Label { Text = "days", Location = new Point(206, 92), AutoSize = true });
        _gbSchedule.Controls.AddRange(new Control[] { _cmbInterval, _lblDayCaption, _cmbDay, _lblTimeCaption, _dtpTime, _numKeep });
        _pBackup.Controls.Add(_gbSchedule);

        _lblBackupSummary.SetBounds(18, 260, 604, 90);
        _pBackup.Controls.Add(_lblBackupSummary);
    }

    /// <summary>A second fixed disk if there is one (survives a Windows disk failure), else the system disk.</summary>
    private static string DefaultBackupDir()
    {
        string system = Path.GetPathRoot(Environment.SystemDirectory);
        var other = DriveInfo.GetDrives().FirstOrDefault(d =>
        {
            try { return d.DriveType == DriveType.Fixed && d.IsReady && !d.Name.Equals(system, StringComparison.OrdinalIgnoreCase); }
            catch (IOException) { return false; }
        });
        return Path.Combine(other?.Name ?? system, "NASBackup");
    }

    private string IntervalValue => Intervals[Math.Max(0, _cmbInterval.SelectedIndex)].Value;

    private bool BackupOn => _chkBackup.Checked && SqlIsLocal();

    /// <summary>Backups are written by SQL Server itself, so they are only scheduled for an instance on this computer (same rule as install.ps1).</summary>
    private bool SqlIsLocal()
    {
        if (_rbInstallNew.Checked) return true;
        string s = _txtServer.Text.Trim();
        return _instances.Any(i => i.Server.Equals(s, StringComparison.OrdinalIgnoreCase) || (".\\" + i.Name).Equals(s, StringComparison.OrdinalIgnoreCase)
                                   || (i.Name == "MSSQLSERVER" && (s == "." || s.Equals("(local)", StringComparison.OrdinalIgnoreCase) || s.Equals("localhost", StringComparison.OrdinalIgnoreCase))));
    }

    private void UpdateBackupPage()
    {
        bool local = SqlIsLocal();
        _chkBackup.Enabled = local;
        bool on = local && _chkBackup.Checked;
        _gbBackupDir.Enabled = _gbSchedule.Enabled = on;

        string interval = IntervalValue;
        bool weekly = interval == "Weekly", hourly = interval.EndsWith("h");
        _lblDayCaption.Visible = _cmbDay.Visible = weekly;
        _lblTimeCaption.Text = hourly ? "Starting at:" : "At:";

        string db = _txtDb.Text.Trim(), time = _dtpTime.Value.ToString("HH:mm");
        string when = interval switch
        {
            "Daily" => "every day at " + time,
            "Weekly" => "every " + Days[Math.Max(0, _cmbDay.SelectedIndex)].Text + " at " + time,
            _ => Intervals[Math.Max(0, _cmbInterval.SelectedIndex)].Text.ToLowerInvariant() + " (from " + time + ")",
        };
        if (!local)
        {
            _lblBackupSummary.ForeColor = Color.Firebrick;
            _lblBackupSummary.Text = "Automatic backup is only available when SQL Server runs on this computer. " +
                                     "Set up backups on the SQL Server computer instead (for example with a SQL Agent job or Windows Task Scheduler).";
        }
        else if (!on)
        {
            _lblBackupSummary.ForeColor = Color.Firebrick;
            _lblBackupSummary.Text = "No automatic backup. You can still download a company backup from Settings > Companies & backup in the app, " +
                                     "but a disk failure or a deleted file can lose all bills since the last manual backup.";
        }
        else
        {
            _lblBackupSummary.ForeColor = SystemColors.ControlText;
            _lblBackupSummary.Text = "A full SQL Server backup of " + db + " is saved " + when + " as " + db + "_yyyyMMdd_HHmmss.bak. " +
                                     "Backups older than " + _numKeep.Value + " days are deleted. If the computer is off at that time, " +
                                     "the backup runs as soon as it is switched on. Setup makes the first backup right away to check it works.";
        }
    }

    private bool ValidateBackup()
    {
        if (!SqlIsLocal() || !_chkBackup.Checked) return true;
        string dir = _txtBackupDir.Text.Trim();
        if (dir.Length > 3) dir = dir.TrimEnd('\\');
        bool ok;
        try { ok = Path.IsPathRooted(dir) && ((dir.Length >= 3 && dir[1] == ':' && dir[2] == '\\') || dir.StartsWith(@"\\")); }
        catch (ArgumentException) { ok = false; }
        if (!ok) { Warn("Enter a full backup folder, for example D:\\NASBackup or \\\\server\\share\\NASBackup."); _txtBackupDir.Focus(); return false; }
        string install = _txtDir.Text.Trim().TrimEnd('\\') + "\\";
        if ((dir + "\\").StartsWith(install, StringComparison.OrdinalIgnoreCase))
        { Warn("The backup folder cannot be inside the install folder (it is replaced on upgrade)."); _txtBackupDir.Focus(); return false; }
        if (dir.StartsWith(@"\\") &&
            MessageBox.Show(this, "Backups to a network folder are written by this computer's account (" + Environment.MachineName + "$), not by your user.\r\n\r\n" +
                                  "Make sure that account may write to the share, or the backups will fail. Continue?",
                            Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
        _txtBackupDir.Text = dir;
        return true;
    }

    private void BuildProgress()
    {
        _lblStep.SetBounds(16, 12, 608, 20);
        _lblStep.Font = new Font(Font, FontStyle.Bold);
        _progress.SetBounds(16, 38, 608, 20);
        _txtLog.SetBounds(16, 68, 608, 276);
        _txtLog.Multiline = true;
        _txtLog.ReadOnly = true;
        _txtLog.WordWrap = false;
        _txtLog.ScrollBars = ScrollBars.Both;
        _txtLog.BackColor = SystemColors.Window;
        _txtLog.Font = new Font("Consolas", 8.25F);
        _pProgress.Controls.AddRange(new Control[] { _lblStep, _progress, _txtLog });
    }

    private void BuildFinish()
    {
        _lblResult.SetBounds(24, 16, 592, 28);
        _lblResult.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
        _lblDetails.SetBounds(24, 54, 592, 200);
        _chkOpen.Text = "Open NAS POS Pro now";
        _chkOpen.SetBounds(24, 262, 300, 24);
        _lnkLog.SetBounds(24, 296, 592, 40);
        _lnkLog.LinkClicked += (s, e) => { if (File.Exists(_logPath)) Process.Start("notepad.exe", Payload.Quote(_logPath)); };
        _pFinish.Controls.AddRange(new Control[] { _lblResult, _lblDetails, _chkOpen, _lnkLog });
    }

    // ------------------------------------------------------------------ navigation

    private void ShowPage(int index)
    {
        _page = index;
        for (int i = 0; i < _pages.Length; i++) _pages[i].Visible = i == index;
        _btnBack.Enabled = index is PageSql or PageApp or PageBackup;
        _btnNext.Enabled = index != PageProgress;
        _btnCancel.Enabled = index != PageProgress;
        _btnCancel.Visible = index != PageFinish;
        _btnNext.Text = index switch { PageBackup => "Install", PageFinish => "Finish", _ => "Next >" };

        (_headTitle.Text, _headText.Text) = index switch
        {
            PageWelcome => ("Welcome to NAS POS Pro Setup", "GST billing and inventory - installed as a web application on IIS"),
            PageSql => ("SQL Server", "Choose the SQL Server that will hold the NAS POS Pro database"),
            PageApp => ("Database and web site", "Login, database and IIS settings"),
            PageBackup => ("Automatic backup", "Where and how often the " + _txtDb.Text.Trim() + " database is backed up"),
            PageProgress => ("Installing", "Please wait - this can take several minutes"),
            _ => ("Setup finished", ""),
        };
        if (index == PageApp) _lblServerValue.Text = _rbInstallNew.Checked ? "New SQL Server Express instance" : _txtServer.Text.Trim();
        if (index == PageBackup) UpdateBackupPage();
    }

    private async Task NextAsync()
    {
        switch (_page)
        {
            case PageWelcome:
                ShowPage(PageSql);
                break;
            case PageSql:
                if (_rbExisting.Checked)
                {
                    if (string.IsNullOrWhiteSpace(_txtServer.Text)) { Warn("Select a SQL Server or choose to install a new one."); return; }
                    if (_testedKey != AuthKey() && !await TestConnectionAsync()) return;
                }
                ShowPage(PageApp);
                break;
            case PageApp:
                if (!ValidateApp()) return;
                ShowPage(PageBackup);
                break;
            case PageBackup:
                if (!ValidateBackup()) return;
                await RunInstallAsync();
                break;
            case PageFinish:
                if (_chkOpen.Checked && _appUrl != null) { try { Process.Start(_appUrl); } catch { /* no default browser */ } }
                Close();
                break;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_running)
        {
            e.Cancel = true;
            MessageBox.Show(this, "Setup is still running. Please wait until it finishes.", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!_done && e.CloseReason == CloseReason.UserClosing &&
            MessageBox.Show(this, "NAS POS Pro is not installed yet. Exit setup?", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }

    private void Warn(string text) => MessageBox.Show(this, text, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    // ------------------------------------------------------------------ SQL page

    private void RefreshInstances()
    {
        _instances = SqlInstanceInfo.Detect();
        _lvInstances.Items.Clear();
        foreach (var i in _instances)
            _lvInstances.Items.Add(new ListViewItem(new[] { i.Name, i.Server, i.VersionText, i.Status }) { Tag = i });

        bool any = _instances.Count > 0;
        _rbExisting.Enabled = any || _txtServer.Text.Length > 0;
        if (!any)
        {
            _lvInstances.Items.Add(new ListViewItem(new[] { "(none found)", "", "", "" }) { ForeColor = SystemColors.GrayText });
            _rbInstallNew.Checked = true;
        }
        else
        {
            var pick = _lvInstances.Items.Cast<ListViewItem>()
                .FirstOrDefault(it => ((SqlInstanceInfo)it.Tag).Server.Equals(_txtServer.Text, StringComparison.OrdinalIgnoreCase))
                ?? _lvInstances.Items.Cast<ListViewItem>().FirstOrDefault(it => ((SqlInstanceInfo)it.Tag).Status == "Running")
                ?? _lvInstances.Items[0];
            pick.Selected = true;
            if (!_rbInstallNew.Checked) _rbExisting.Checked = true;
        }
        _rbInstallNew.Text = any
            ? "Install a new SQL Server 2022 Express instance (free)"
            : "No SQL Server found - install SQL Server 2022 Express (free)";
        UpdateSqlPage();
    }

    private void UpdateSqlPage()
    {
        bool existing = _rbExisting.Checked;
        _lvInstances.Enabled = existing && _instances.Count > 0;
        _txtServer.Enabled = existing;
        _gbAuth.Enabled = existing;
        _txtAdminUser.Enabled = _txtAdminPwd.Enabled = _rbSqlAuth.Checked;
        if (!existing) SetTest("A new instance is installed with Windows authentication for setup; nothing to test.", SystemColors.GrayText);
        else if (_testedKey == null) SetTest("", SystemColors.ControlText);
    }

    private string AuthKey() => _txtServer.Text.Trim() + "|" + (_rbSqlAuth.Checked ? _txtAdminUser.Text + "|" + _txtAdminPwd.Text : "win");

    private void ClearTest()
    {
        _testedKey = null;
        if (_rbExisting.Checked) SetTest("", SystemColors.ControlText);
    }

    private void SetTest(string text, Color color) { _lblTest.Text = text; _lblTest.ForeColor = color; }

    private string AdminConnectionString()
    {
        var b = new SqlConnectionStringBuilder { DataSource = _txtServer.Text.Trim(), InitialCatalog = "master", ConnectTimeout = 15, TrustServerCertificate = true };
        if (_rbSqlAuth.Checked) { b.UserID = _txtAdminUser.Text.Trim(); b.Password = _txtAdminPwd.Text; }
        else b.IntegratedSecurity = true;
        return b.ConnectionString;
    }

    private async Task<bool> TestConnectionAsync()
    {
        string key = AuthKey(), cs = AdminConnectionString(), server = _txtServer.Text.Trim();
        var inst = _instances.FirstOrDefault(i => i.Server.Equals(server, StringComparison.OrdinalIgnoreCase));
        _btnTest.Enabled = _btnNext.Enabled = false;
        UseWaitCursor = true;
        SetTest(inst != null && inst.Status != "Running" ? "Starting " + inst.Service + "..." : "Connecting...", SystemColors.ControlText);
        try
        {
            var r = await Task.Run(() =>
            {
                if (inst != null && inst.Status != "Running") inst.Start();
                using (var cn = new SqlConnection(cs))
                {
                    cn.Open();
                    var cmd = cn.CreateCommand();
                    cmd.CommandText = "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')), CONVERT(nvarchar(128), SERVERPROPERTY('Edition')), " +
                                      "IS_SRVROLEMEMBER('sysadmin'), CONVERT(int, SERVERPROPERTY('IsIntegratedSecurityOnly'))";
                    using (var rd = cmd.ExecuteReader())
                    {
                        rd.Read();
                        return (Version: rd.GetString(0), Edition: rd.GetString(1), Admin: !rd.IsDBNull(2) && rd.GetInt32(2) == 1, WindowsOnly: rd.GetInt32(3) == 1);
                    }
                }
            });
            if (inst != null) { inst.Refresh(); RefreshInstanceRow(inst); }
            string what = SqlInstanceInfo.ProductName(r.Version) + " " + r.Edition + " (" + r.Version + ")";
            if (!r.Admin)
            {
                SetTest("Connected to " + what + ", but this login is not a sysadmin. Use an administrator login (e.g. sa).", Color.Firebrick);
                return false;
            }
            SetTest("Connected: " + what + ". Administrator: yes." +
                    (r.WindowsOnly ? "\r\nSQL Server authentication is off - setup will turn it on (the SQL service restarts once)." : ""),
                    Color.DarkGreen);
            _testedKey = key;
            return true;
        }
        catch (Exception ex)
        {
            SetTest("Could not connect: " + ex.GetBaseException().Message, Color.Firebrick);
            return false;
        }
        finally
        {
            UseWaitCursor = false;
            _btnTest.Enabled = _btnNext.Enabled = true;
        }
    }

    private void RefreshInstanceRow(SqlInstanceInfo inst)
    {
        foreach (ListViewItem it in _lvInstances.Items)
            if (it.Tag == inst) it.SubItems[3].Text = inst.Status;
    }

    // ------------------------------------------------------------------ app page

    private bool ValidateApp()
    {
        string db = _txtDb.Text.Trim(), login = _txtLogin.Text.Trim(), dir = _txtDir.Text.Trim().TrimEnd('\\');
        if (!IdentifierRx.IsMatch(db)) { Warn("Database name may contain letters, digits and _ only, and must start with a letter."); _txtDb.Focus(); return false; }
        if (!IdentifierRx.IsMatch(login) || new[] { "sa", "dbo", "guest" }.Contains(login, StringComparer.OrdinalIgnoreCase))
        { Warn("SQL login may contain letters, digits and _ only, and cannot be sa, dbo or guest."); _txtLogin.Focus(); return false; }
        if (_txtPwd.Text.Length == 0) { Warn("Enter a password for the SQL login."); _txtPwd.Focus(); return false; }
        if (!int.TryParse(_txtPort.Text.Trim(), out int port) || port < 1 || port > 65535) { Warn("Port must be a number between 1 and 65535."); _txtPort.Focus(); return false; }
        bool rooted;
        try { rooted = Path.IsPathRooted(dir) && dir.Length > 3 && dir[1] == ':'; } catch (ArgumentException) { rooted = false; }
        if (!rooted) { Warn("Enter a full install folder path, for example C:\\inetpub\\NasPosPro."); _txtDir.Focus(); return false; }
        _txtDir.Text = dir;
        return true;
    }

    private void LoadPreviousInstall()
    {
        // An upgrade keeps the previous folder, port, server and database.
        string state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"NasPosPro\install.json");
        if (!File.Exists(state)) return;
        try
        {
            string json = File.ReadAllText(state);
            string Get(string name)
            {
                var m = Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"?([^\",\\r\\n}]*)");
                return m.Success ? Regex.Unescape(m.Groups[1].Value).Trim() : "";
            }
            if (Get("InstallDir") is { Length: > 0 } d) _txtDir.Text = d;
            if (Get("Port") is { Length: > 0 } p) _txtPort.Text = p;
            // Installs from before the NAS login existed used another database name; those move to NASDB.
            if (Get("AppSqlUser") is { Length: > 0 } u)
            {
                _txtLogin.Text = u;
                if (Get("DbName") is { Length: > 0 } db) _txtDb.Text = db;
            }
            if (Get("SqlInstance") is { Length: > 0 } s) _txtServer.Text = s;

            if (Get("BackupDir") is { Length: > 0 } bd) _txtBackupDir.Text = bd;
            if (Get("BackupInterval") is { Length: > 0 } bi)
            {
                int idx = Array.FindIndex(Intervals, i => i.Value.Equals(bi, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) _cmbInterval.SelectedIndex = idx;
                _chkBackup.Checked = !bi.Equals("Off", StringComparison.OrdinalIgnoreCase);
            }
            if (DateTime.TryParseExact(Get("BackupTime"), "HH:mm", null, System.Globalization.DateTimeStyles.None, out var bt)) _dtpTime.Value = DateTime.Today.Add(bt.TimeOfDay);
            int day = Array.FindIndex(Days, x => x.Value.Equals(Get("BackupDay"), StringComparison.OrdinalIgnoreCase));
            if (day >= 0) _cmbDay.SelectedIndex = day;
            if (int.TryParse(Get("BackupKeepDays"), out int keep) && keep >= 1 && keep <= 3650) _numKeep.Value = keep;
        }
        catch { /* unreadable state: use defaults */ }
    }

    // ------------------------------------------------------------------ install

    private async Task RunInstallAsync()
    {
        _running = true;
        ShowPage(PageProgress);
        _progress.Style = ProgressBarStyle.Marquee;
        _lblStep.Text = "Extracting setup files...";
        string work = Payload.NewWorkDir();
        int code = 1;
        try
        {
            await Task.Run(() => Payload.Extract(work));

            var args = new List<string>
            {
                "-Gui", "-Quiet", "-NoBrowser",
                "-InstallDir", _txtDir.Text.Trim(),
                "-Port", _txtPort.Text.Trim(),
                "-DbName", _txtDb.Text.Trim(),
                "-AppSqlUser", _txtLogin.Text.Trim(),
            };
            if (_rbInstallNew.Checked) args.Add("-InstallSqlExpress");
            else
            {
                args.AddRange(new[] { "-SqlInstance", _txtServer.Text.Trim() });
                if (_rbSqlAuth.Checked) args.AddRange(new[] { "-SqlUser", _txtAdminUser.Text.Trim() });
            }
            if (BackupOn)
                args.AddRange(new[]
                {
                    "-BackupInterval", IntervalValue,
                    "-BackupDir", _txtBackupDir.Text.Trim(),
                    "-BackupTime", _dtpTime.Value.ToString("HH:mm"),
                    "-BackupDay", Days[Math.Max(0, _cmbDay.SelectedIndex)].Value,
                    "-BackupKeepDays", ((int)_numKeep.Value).ToString(),
                });
            else args.AddRange(new[] { "-BackupInterval", "Off" });

            var psi = Payload.PowerShell(Path.Combine(work, "install.ps1"), args, work);
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            // Passwords go through the environment, not the command line (visible to other processes).
            psi.EnvironmentVariables["NASSETUP_APP_PASSWORD"] = _txtPwd.Text;
            if (_rbExisting.Checked && _rbSqlAuth.Checked) psi.EnvironmentVariables["NASSETUP_SQL_PASSWORD"] = _txtAdminPwd.Text;

            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null) BeginInvoke(new Action(() => OnScriptLine(e.Data))); };
                p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) BeginInvoke(new Action(() => AppendLog(e.Data))); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                code = await Task.Run(() => { p.WaitForExit(); return p.ExitCode; });
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            AppendLog("Setup could not run: " + ex);
        }
        finally
        {
            Payload.Cleanup(work);
        }
        // Lines queued with BeginInvoke run before this continuation, so _error / _appUrl are complete here.
        _running = false;
        _done = true;
        ExitCode = code;
        ShowFinish(code == 0);
    }

    private void OnScriptLine(string line)
    {
        if (line.StartsWith("##PROGRESS|"))
        {
            var parts = line.Split(new[] { '|' }, 3);
            if (int.TryParse(parts[1], out int pct))
            {
                _progress.Style = ProgressBarStyle.Continuous;
                _progress.Value = Math.Max(0, Math.Min(100, pct));
            }
            if (parts.Length > 2) _lblStep.Text = parts[2];
        }
        else if (line.StartsWith("##LOG|")) _logPath = line.Substring(6);
        else if (line.StartsWith("##URL|")) _appUrl = line.Substring(6);
        else if (line.StartsWith("##ERROR|")) _error = line.Substring(8);
        else if (line == "##RESTART") _restartNeeded = true;
        else if (line.StartsWith("##WARN|")) _warnings.Add(line.Substring(7));
        else AppendLog(line);
    }

    private void AppendLog(string line) => _txtLog.AppendText(line + Environment.NewLine);

    private void ShowFinish(bool ok)
    {
        ShowPage(PageFinish);
        string port = _txtPort.Text.Trim();
        if (ok)
        {
            _lblResult.Text = _warnings.Count == 0 ? "NAS POS Pro has been installed." : "NAS POS Pro has been installed - with warnings.";
            _lblResult.ForeColor = _warnings.Count == 0 ? Color.DarkGreen : Color.DarkOrange;
            string backup = BackupOn
                ? _cmbInterval.Text.ToLowerInvariant() + (IntervalValue.EndsWith("h") ? "" : " at " + _dtpTime.Value.ToString("HH:mm")) + " to " + _txtBackupDir.Text.Trim() + " (kept " + _numKeep.Value + " days)"
                : "off";
            _lblDetails.Text =
                "Open on this computer:   " + (_appUrl ?? "http://localhost:" + port + "/") + "\r\n" +
                "Other computers (LAN):   http://" + Environment.MachineName + ":" + port + "/\r\n\r\n" +
                "SQL Server:   " + (_rbInstallNew.Checked ? "new SQL Server Express instance" : _txtServer.Text.Trim()) + "\r\n" +
                "Database:      " + _txtDb.Text.Trim() + "     SQL login: " + _txtLogin.Text.Trim() + "\r\n" +
                "Backup:         " + backup + "\r\n\r\n" +
                string.Concat(_warnings.Select(w => "Warning: " + w + "\r\n")) +
                "A shortcut 'NAS POS Pro' was added to the desktop. On a new database the first sign-in opens the company setup screen." +
                (_restartNeeded ? "\r\n\r\nWindows asked for a restart to finish installing components. Please restart soon." : "");
            _chkOpen.Visible = true;
            _chkOpen.Checked = true;
        }
        else
        {
            _lblResult.Text = "Setup did not complete.";
            _lblResult.ForeColor = Color.Firebrick;
            _lblDetails.Text = (_error ?? "The installer stopped with an error. See the log for details.") + "\r\n\r\n" +
                               "Fix the problem and run setup again - it continues where it stopped and keeps existing data.";
            _chkOpen.Visible = false;
            _chkOpen.Checked = false;
        }
        _lnkLog.Visible = _logPath != null;
        _lnkLog.Text = "Installation log: " + _logPath;
        _lnkLog.LinkArea = new LinkArea(0, _lnkLog.Text.Length);
    }
}

/// <summary>A SQL Server instance installed on this computer (from the registry).</summary>
internal sealed class SqlInstanceInfo
{
    public string Name { get; private set; }
    public string Server { get; private set; }
    public string Service { get; private set; }
    public string Version { get; private set; }
    public string Edition { get; private set; }
    public string Status { get; private set; }

    public string VersionText => string.IsNullOrEmpty(Version) ? Edition : ProductName(Version) + " " + (Edition ?? "").Replace(" Edition", "").Replace(" (64-bit)", "");

    public static List<SqlInstanceInfo> Detect()
    {
        var list = new List<SqlInstanceInfo>();
        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var names = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL"))
        {
            if (names == null) return list;
            foreach (string name in names.GetValueNames())
            {
                string id = names.GetValue(name) as string;
                var i = new SqlInstanceInfo
                {
                    Name = name,
                    Server = name.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ? "localhost" : @"localhost\" + name,
                    Service = name.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ? "MSSQLSERVER" : "MSSQL$" + name,
                };
                using (var setup = id == null ? null : hklm.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\" + id + @"\Setup"))
                {
                    i.Version = setup?.GetValue("Version") as string;
                    i.Edition = setup?.GetValue("Edition") as string;
                }
                i.Refresh();
                list.Add(i);
            }
        }
        return list;
    }

    public void Refresh()
    {
        try { using (var sc = new ServiceController(Service)) Status = sc.Status.ToString(); }
        catch (InvalidOperationException) { Status = "Not found"; }
    }

    public void Start()
    {
        using (var sc = new ServiceController(Service))
        {
            if (sc.Status != ServiceControllerStatus.Running && sc.Status != ServiceControllerStatus.StartPending) sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
        }
    }

    public static string ProductName(string version)
    {
        int major = int.TryParse((version ?? "").Split('.')[0], out int m) ? m : 0;
        return major switch
        {
            17 => "SQL Server 2025",
            16 => "SQL Server 2022",
            15 => "SQL Server 2019",
            14 => "SQL Server 2017",
            13 => "SQL Server 2016",
            12 => "SQL Server 2014",
            11 => "SQL Server 2012",
            0 => "SQL Server",
            _ => "SQL Server " + version,
        };
    }
}
