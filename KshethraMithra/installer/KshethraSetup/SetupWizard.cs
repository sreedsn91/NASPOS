using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KshethraSetup;

/// <summary>
/// Setup wizard: Welcome -> SQL Server (pick a local instance or install SQL Express) -> database / app login / IIS port
/// -> progress (runs install.ps1 -Quiet and shows its output) -> finish.
/// </summary>
internal sealed class SetupWizard : Form
{
    private static readonly Regex PlainName = new("^[A-Za-z_][A-Za-z0-9_]{0,99}$");
    private static readonly Regex InstanceName = new("^[A-Za-z][A-Za-z0-9_]{0,15}$");
    private const int ExpectedSteps = 14;   // "==> " lines install.ps1 prints on a full install, plus extraction
    private static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KshethraMithra");

    public int ExitCode { get; private set; } = 1;

    private readonly Label _title = new() { Font = new Font("Segoe UI", 13F, FontStyle.Bold), AutoSize = true, Location = new Point(20, 12) };
    private readonly Label _subtitle = new() { AutoSize = true, Location = new Point(22, 42), ForeColor = Color.DimGray };
    private readonly Panel[] _pages = new Panel[5];
    private readonly Button _back = new() { Text = "< Back", Size = new Size(90, 30) };
    private readonly Button _next = new() { Text = "Next >", Size = new Size(90, 30) };
    private readonly Button _cancel = new() { Text = "Cancel", Size = new Size(90, 30) };
    private int _page;
    private bool _running;

    // Welcome
    private readonly Label _welcomeText = new() { Location = new Point(0, 0), Size = new Size(620, 300) };

    // SQL Server
    private readonly ListView _instances = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, Location = new Point(0, 22), Size = new Size(520, 120) };
    private readonly Button _refresh = new() { Text = "Refresh", Location = new Point(530, 22), Size = new Size(90, 28) };
    private readonly RadioButton _useExisting = new() { Text = "Use the selected SQL Server", AutoSize = true, Location = new Point(0, 152) };
    private readonly RadioButton _useOther = new() { Text = "Use another server:", AutoSize = true, Location = new Point(0, 178) };
    private readonly TextBox _otherServer = new() { Location = new Point(170, 176), Size = new Size(250, 23) };
    private readonly RadioButton _installNew = new() { Text = "Install new SQL Server 2022 Express, instance:", AutoSize = true, Location = new Point(0, 204) };
    private readonly TextBox _newInstance = new() { Location = new Point(320, 202), Size = new Size(100, 23) };
    private readonly RadioButton _winAuth = new() { AutoSize = true, Location = new Point(12, 22), Checked = true };
    private readonly RadioButton _sqlAuth = new() { Text = "SQL login:", AutoSize = true, Location = new Point(12, 48) };
    private readonly TextBox _adminUser = new() { Text = "sa", Location = new Point(100, 46), Size = new Size(110, 23) };
    private readonly TextBox _adminPassword = new() { UseSystemPasswordChar = true, Location = new Point(290, 46), Size = new Size(150, 23) };
    private readonly GroupBox _authBox = new() { Text = "Administrator sign-in used by setup", Location = new Point(0, 234), Size = new Size(620, 80) };
    private readonly Button _test = new() { Text = "Test connection", Location = new Point(0, 322), Size = new Size(120, 28) };
    private readonly Label _testResult = new() { Location = new Point(128, 328), Size = new Size(492, 40) };

    // Database / app
    private readonly TextBox _dbName = new() { Text = "KMDB", Location = new Point(180, 20), Size = new Size(200, 23) };
    private readonly TextBox _appLogin = new() { Text = "KM", Location = new Point(180, 56), Size = new Size(200, 23) };
    private readonly TextBox _appPassword = new() { Text = "admin@123", UseSystemPasswordChar = true, Location = new Point(180, 92), Size = new Size(200, 23) };
    private readonly CheckBox _showPassword = new() { Text = "Show", AutoSize = true, Location = new Point(390, 94) };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 4400, Location = new Point(180, 152), Size = new Size(90, 23) };
    private readonly TextBox _installDir = new() { Location = new Point(180, 188), Size = new Size(340, 23) };
    private readonly Button _browse = new() { Text = "Browse...", Location = new Point(528, 187), Size = new Size(90, 26) };

    // Progress
    private readonly Label _stepLabel = new() { Location = new Point(0, 0), Size = new Size(620, 22) };
    private readonly ProgressBar _progress = new() { Location = new Point(0, 26), Size = new Size(620, 22), Maximum = ExpectedSteps };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = false, Location = new Point(0, 58), Size = new Size(620, 300), Font = new Font("Consolas", 8.5F), BackColor = Color.White };

    // Finish
    private readonly Label _finishText = new() { Location = new Point(0, 0), Size = new Size(620, 200) };
    private readonly LinkLabel _finishLink = new() { AutoSize = true, Location = new Point(0, 210) };
    private readonly CheckBox _openApp = new() { Text = "Open Kshethra Mithra now", AutoSize = true, Checked = true, Location = new Point(0, 250) };

    private string _url = "";
    private bool _restartNeeded;
    private string _lastError = "";

    public SetupWizard()
    {
        Text = "Kshethra Mithra Setup";
        Font = new Font("Segoe UI", 9F);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(660, 520);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* default icon */ }

        var header = new Panel { Dock = DockStyle.Top, Height = 70, BackColor = Color.White };
        header.Controls.AddRange(new Control[] { _title, _subtitle });
        var headerLine = new Label { Dock = DockStyle.Top, Height = 1, BackColor = Color.Silver };
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 52 };
        var footerLine = new Label { Dock = DockStyle.Bottom, Height = 1, BackColor = Color.Silver };
        _cancel.Location = new Point(550, 11); _next.Location = new Point(450, 11); _back.Location = new Point(355, 11);
        footer.Controls.AddRange(new Control[] { _back, _next, _cancel });

        for (int i = 0; i < _pages.Length; i++)
        {
            _pages[i] = new Panel { Location = new Point(20, 86), Size = new Size(620, 375), Visible = false };
            Controls.Add(_pages[i]);
        }
        Controls.AddRange(new Control[] { headerLine, header, footerLine, footer });

        BuildWelcome();
        BuildSql();
        BuildDatabase();
        _pages[3].Controls.AddRange(new Control[] { _stepLabel, _progress, _log });
        _pages[4].Controls.AddRange(new Control[] { _finishText, _finishLink, _openApp });

        _back.Click += (_, _) => ShowPage(_page - 1);
        _next.Click += async (_, _) => await NextAsync();
        _cancel.Click += (_, _) => Close();
        FormClosing += OnClosing;
        AcceptButton = _next;

        LoadPreviousInstall();
        ShowPage(0);
    }

    // ------------------------------------------------------------------ pages

    private void BuildWelcome()
    {
        _welcomeText.Text =
            "This wizard installs Kshethra Mithra on this computer:\r\n\r\n" +
            "  1.  Turns on IIS (Internet Information Services).\r\n" +
            "  2.  Installs the .NET 9 Hosting Bundle (included in this setup).\r\n" +
            "  3.  Uses a SQL Server on this computer, or installs SQL Server 2022 Express.\r\n" +
            "  4.  Creates the SQL login and the database, with all the tables.\r\n" +
            "  5.  Hosts the application as an IIS web site.\r\n\r\n" +
            "Installing SQL Server Express needs an internet connection (about 250 MB) unless it is bundled in this setup. " +
            "The whole setup can take 10-20 minutes.\r\n\r\n" +
            "Close other programs, then click Next.";
        _pages[0].Controls.Add(_welcomeText);
    }

    private void BuildSql()
    {
        _instances.Columns.Add("Instance", 150);
        _instances.Columns.Add("Version", 200);
        _instances.Columns.Add("Edition", 100);
        _instances.Columns.Add("Status", 65);
        _winAuth.Text = $"Windows authentication ({Environment.UserDomainName}\\{Environment.UserName})";
        _authBox.Controls.AddRange(new Control[] { _winAuth, _sqlAuth, _adminUser, new Label { Text = "Password:", AutoSize = true, Location = new Point(222, 49) }, _adminPassword });
        _pages[1].Controls.AddRange(new Control[]
        {
            new Label { Text = "SQL Server instances found on this computer:", AutoSize = true, Location = new Point(0, 0) },
            _instances, _refresh, _useExisting, _useOther, _otherServer, _installNew, _newInstance, _authBox, _test, _testResult,
        });

        _refresh.Click += (_, _) => RefreshInstances();
        _test.Click += async (_, _) => await TestAsync();
        _instances.SelectedIndexChanged += (_, _) => { if (_instances.SelectedItems.Count > 0) _useExisting.Checked = true; };
        _otherServer.TextChanged += (_, _) => { if (_otherServer.Focused) _useOther.Checked = true; };
        _newInstance.TextChanged += (_, _) => { if (_newInstance.Focused) _installNew.Checked = true; };
        foreach (var rb in new[] { _useExisting, _useOther, _installNew, _winAuth, _sqlAuth }) rb.CheckedChanged += (_, _) => UpdateSqlState();
        RefreshInstances();
    }

    private void BuildDatabase()
    {
        Label L(string text, int y) => new() { Text = text, AutoSize = true, Location = new Point(0, y + 3) };
        _installDir.Text = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", @"inetpub\KshethraMithra");
        _pages[2].Controls.AddRange(new Control[]
        {
            L("Database name:", 20), _dbName,
            L("Application SQL login:", 56), _appLogin,
            L("Login password:", 92), _appPassword, _showPassword,
            new Label { Text = "The web site connects to SQL Server with this login. It gets read/write access to the database only. " +
                              "Setup creates the database and all its tables.",
                        Location = new Point(180, 120), Size = new Size(440, 32), ForeColor = Color.DimGray },
            L("IIS web site port:", 152), _port,
            L("Install folder:", 188), _installDir, _browse,
        });
        _showPassword.CheckedChanged += (_, _) => _appPassword.UseSystemPasswordChar = !_showPassword.Checked;
        _browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _installDir.Text, Description = "Folder for the web site files" };
            if (dlg.ShowDialog(this) == DialogResult.OK) _installDir.Text = dlg.SelectedPath;
        };
    }

    private void ShowPage(int page)
    {
        _page = page;
        for (int i = 0; i < _pages.Length; i++) _pages[i].Visible = i == page;
        (_title.Text, _subtitle.Text) = page switch
        {
            0 => ("Welcome to Kshethra Mithra Setup", "Temple management - IIS + SQL Server installation"),
            1 => ("SQL Server", "Choose the SQL Server that will hold the database"),
            2 => ("Database and web site", "Database, application login and IIS settings"),
            3 => ("Installing", "Please wait while Kshethra Mithra is installed"),
            _ => ("Setup finished", ""),
        };
        _back.Visible = page is 1 or 2;
        _next.Text = page == 2 ? "Install" : page == 4 ? "Finish" : "Next >";
        _next.Enabled = page != 3;
        _cancel.Visible = page != 4;
        _cancel.Enabled = page != 3;
        if (page == 1) UpdateSqlState();
    }

    private async Task NextAsync()
    {
        switch (_page)
        {
            case 0: ShowPage(1); break;
            case 1: if (await ValidateSqlAsync()) ShowPage(2); break;
            case 2: if (ValidateDatabase()) { ShowPage(3); await InstallAsync(); } break;
            case 4:
                if (ExitCode == 0 && _openApp.Checked && _url.Length > 0) Process.Start(_url);
                Close();
                break;
        }
    }

    // ------------------------------------------------------------------ SQL page

    private void RefreshInstances()
    {
        _instances.Items.Clear();
        List<SqlInstance> found;
        try { found = SqlDiscovery.LocalInstances(); }
        catch (Exception ex) { found = new List<SqlInstance>(); _testResult.Text = "Could not read installed SQL Servers: " + ex.Message; }
        foreach (var i in found)
            _instances.Items.Add(new ListViewItem(new[] { i.Server, i.Version, i.Edition, i.Status }) { Tag = i });

        bool any = found.Count > 0;
        _instances.Enabled = any;
        _useExisting.Enabled = any;
        if (!any)
        {
            _instances.Items.Add(new ListViewItem("No SQL Server found - a new one will be installed."));
            _installNew.Checked = true;
        }
        else
        {
            var pick = _instances.Items.Cast<ListViewItem>().FirstOrDefault(x => ((SqlInstance)x.Tag).Status == "Running") ?? _instances.Items[0];
            pick.Selected = true;
            if (!_useOther.Checked) _useExisting.Checked = true;
        }
        // Suggest an instance name that is not taken yet.
        if (_newInstance.Text.Length == 0 || found.Any(i => i.Name.Equals(_newInstance.Text, StringComparison.OrdinalIgnoreCase)))
            _newInstance.Text = found.Any(i => i.Name.Equals("SQLEXPRESS", StringComparison.OrdinalIgnoreCase)) ? "KMSQL" : "SQLEXPRESS";
        UpdateSqlState();
    }

    private void UpdateSqlState()
    {
        bool fresh = _installNew.Checked;
        _authBox.Enabled = !fresh;
        _test.Enabled = !fresh;
        _adminUser.Enabled = _adminPassword.Enabled = _sqlAuth.Checked;
        if (fresh) _testResult.Text = "Setup installs SQL Server Express and signs in with Windows authentication.";
    }

    private SqlInstance? SelectedInstance => _instances.SelectedItems.Count > 0 ? _instances.SelectedItems[0].Tag as SqlInstance : null;

    private string ChosenServer => _useOther.Checked ? _otherServer.Text.Trim() : SelectedInstance?.Server ?? "";

    private async Task<SqlCheck?> TestAsync()
    {
        string server = ChosenServer;
        if (server.Length == 0) { _testResult.Text = "Choose a SQL Server first."; return null; }
        string? user = _sqlAuth.Checked ? _adminUser.Text.Trim() : null;
        string password = _adminPassword.Text;
        string? service = _useExisting.Checked ? SelectedInstance?.Service : null;

        UseWaitCursor = true; _next.Enabled = _back.Enabled = _test.Enabled = false;
        _testResult.ForeColor = SystemColors.ControlText;
        _testResult.Text = "Connecting to " + server + "...";
        try
        {
            var check = await Task.Run(() =>
            {
                if (service != null) { try { SqlDiscovery.EnsureRunning(service); } catch { /* the connection error explains it */ } }
                return SqlDiscovery.Test(server, user, password);
            });
            if (!check.Connected) { _testResult.ForeColor = Color.Firebrick; _testResult.Text = "Could not connect: " + check.Error; }
            else if (!check.IsSysAdmin) { _testResult.ForeColor = Color.Firebrick; _testResult.Text = "Connected, but this sign-in is not a sysadmin. Setup needs sysadmin to create the login and database."; }
            else
            {
                _testResult.ForeColor = Color.ForestGreen;
                _testResult.Text = "Connected - " + check.Version + (check.WindowsAuthOnly ? ". SQL Server authentication is off; setup will turn it on and restart SQL Server." : ".");
            }
            if (service != null) RefreshStatus();
            return check;
        }
        finally { UseWaitCursor = false; _next.Enabled = _back.Enabled = true; _test.Enabled = !_installNew.Checked; }
    }

    private void RefreshStatus()
    {
        foreach (ListViewItem item in _instances.Items)
            if (item.Tag is SqlInstance i) item.SubItems[3].Text = i.Status = SqlDiscovery.ServiceStatus(i.Service);
    }

    private async Task<bool> ValidateSqlAsync()
    {
        if (_installNew.Checked)
        {
            if (!InstanceName.IsMatch(_newInstance.Text.Trim())) return Invalid(_newInstance, "Instance name: up to 16 letters, digits or _, starting with a letter.");
            return true;
        }
        if (_sqlAuth.Checked && _adminUser.Text.Trim().Length == 0) return Invalid(_adminUser, "Enter the SQL admin login.");
        var check = await TestAsync();
        if (check == null || !check.Connected || !check.IsSysAdmin) return false;
        if (check.WindowsAuthOnly && !_useExisting.Checked)
        {
            MessageBox.Show(this, "That server accepts Windows logins only, and setup can change this only for a SQL Server on this computer.\r\n\r\n" +
                                  "Turn on 'SQL Server and Windows Authentication mode' in SQL Server Management Studio, restart SQL Server and try again.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ database page

    private bool ValidateDatabase()
    {
        if (!PlainName.IsMatch(_dbName.Text.Trim())) return Invalid(_dbName, "Database name: letters, digits and _ only, starting with a letter.");
        if (!PlainName.IsMatch(_appLogin.Text.Trim())) return Invalid(_appLogin, "Login name: letters, digits and _ only, starting with a letter.");
        if (_appPassword.Text.Length == 0) return Invalid(_appPassword, "Enter a password for the SQL login.");
        string dir = _installDir.Text.Trim();
        if (dir.Length < 4 || !Path.IsPathRooted(dir) || dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return Invalid(_installDir, "Enter a full folder path, e.g. C:\\inetpub\\KshethraMithra.");
        return true;
    }

    private bool Invalid(Control c, string message)
    {
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        c.Focus();
        return false;
    }

    // ------------------------------------------------------------------ install

    private async Task InstallAsync()
    {
        _running = true;
        int port = (int)_port.Value;
        _url = $"http://localhost:{port}/";
        var args = new List<string>
        {
            "-Quiet", "-NoBrowser",
            "-Port", port.ToString(),
            "-InstallDir", _installDir.Text.Trim(),
            "-DbName", _dbName.Text.Trim(),
            "-AppLogin", _appLogin.Text.Trim(),
        };
        if (_installNew.Checked) args.AddRange(new[] { "-InstallSql", "-NewSqlInstanceName", _newInstance.Text.Trim() });
        else
        {
            args.AddRange(new[] { "-SqlInstance", ChosenServer });
            if (_sqlAuth.Checked) args.AddRange(new[] { "-SqlUser", _adminUser.Text.Trim() });
        }
        string sqlPassword = _sqlAuth.Checked && !_installNew.Checked ? _adminPassword.Text : "";
        string appPassword = _appPassword.Text;

        string work = Program.NewWorkDir();
        try
        {
            SetStep("Extracting setup files...");
            await Task.Run(() => Program.Extract(work));
            _progress.Value = 1;

            var psi = Program.PowerShell(Path.Combine(work, "install.ps1"), args, work);
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = psi.RedirectStandardError = psi.RedirectStandardInput = true;
            // Passwords go through the environment, not the command line, so they do not show in process lists.
            psi.EnvironmentVariables["KSH_SQL_PASSWORD"] = sqlPassword;
            psi.EnvironmentVariables["KSH_APP_PASSWORD"] = appPassword;

            ExitCode = await Task.Run(() =>
            {
                using var p = new Process { StartInfo = psi };
                p.OutputDataReceived += (_, e) => { if (e.Data != null) BeginInvoke(new Action(() => OnOutput(e.Data))); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) BeginInvoke(new Action(() => OnOutput(e.Data))); };
                p.Start();
                p.StandardInput.Close();   // a stray prompt reads end-of-input instead of hanging setup
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                return p.ExitCode;
            });
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            OnOutput("SETUP FAILED: " + ex.Message);
            ExitCode = 1;
        }
        finally
        {
            Program.DeleteWorkDir(work);
            _running = false;
        }
        ShowFinish();
    }

    private void OnOutput(string line)
    {
        _log.AppendText(line + Environment.NewLine);
        string t = line.Trim();
        if (t.StartsWith("==> "))
        {
            SetStep(t.Substring(4));
            if (_progress.Value < _progress.Maximum - 1) _progress.Value++;
        }
        else if (t.StartsWith("SETUP FAILED:")) _lastError = t.Substring("SETUP FAILED:".Length).Trim();
        else if (t.Contains("asked for a restart")) _restartNeeded = true;
    }

    private void SetStep(string text) => _stepLabel.Text = text;

    private void ShowFinish()
    {
        _progress.Value = _progress.Maximum;
        if (ExitCode == 0)
        {
            string server = _installNew.Checked ? @"localhost\" + _newInstance.Text.Trim() : ChosenServer;
            _finishText.Text =
                "Kshethra Mithra is installed and running in IIS.\r\n\r\n" +
                $"  Web address:      {_url}\r\n" +
                $"  Other computers:  http://{Environment.MachineName}:{_port.Value}/\r\n" +
                $"  SQL Server:       {server}\r\n" +
                $"  Database:         {_dbName.Text.Trim()}\r\n" +
                $"  SQL login:        {_appLogin.Text.Trim()}\r\n\r\n" +
                "The first visit opens the temple setup screen, where you create the administrator account." +
                (_restartNeeded ? "\r\n\r\nWindows asked for a restart to finish installing components. Please restart soon." : "");
            _finishLink.Text = _url;
            _finishLink.LinkClicked += (_, _) => Process.Start(_url);
            _openApp.Visible = true;
        }
        else
        {
            _title.Text = "Setup failed";
            _finishText.Text = "Kshethra Mithra could not be installed.\r\n\r\n" + (_lastError.Length > 0 ? _lastError : "See the log for details.") +
                               "\r\n\r\nFix the problem and run this setup again; it continues where it stopped and keeps existing data.";
            _finishLink.Text = "Open the log folder (" + DataDir + ")";
            _finishLink.LinkClicked += (_, _) => { if (Directory.Exists(DataDir)) Process.Start("explorer.exe", "\"" + DataDir + "\""); };
            _openApp.Visible = false;
        }
        ShowPage(4);
        if (ExitCode != 0) _title.Text = "Setup failed";
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_running)
        {
            MessageBox.Show(this, "Setup is still running. Stopping it now could leave IIS or SQL Server half-configured. Please wait until it finishes.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            e.Cancel = true;
        }
        else if (_page < 3 && MessageBox.Show(this, "Cancel Kshethra Mithra setup?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            e.Cancel = true;
    }

    /// <summary>On a re-run (upgrade), start from the previous install's choices.</summary>
    private void LoadPreviousInstall()
    {
        string file = Path.Combine(DataDir, "install.json");
        if (!File.Exists(file)) return;
        try
        {
            string json = File.ReadAllText(file);
            string? Get(string key)
            {
                var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(\"(?<s>(?:[^\"\\\\]|\\\\.)*)\"|(?<n>\\d+))");
                if (!m.Success) return null;
                return m.Groups["s"].Success ? Regex.Unescape(m.Groups["s"].Value) : m.Groups["n"].Value;
            }
            if (Get("InstallDir") is { Length: > 0 } dir) _installDir.Text = dir;
            if (int.TryParse(Get("Port"), out int port) && port is > 0 and <= 65535) _port.Value = port;
            if (Get("DbName") is { Length: > 0 } db) _dbName.Text = db;
            if (Get("AppLogin") is { Length: > 0 } login) _appLogin.Text = login;
            if (Get("SqlInstance") is { Length: > 0 } server)
            {
                var item = _instances.Items.Cast<ListViewItem>().FirstOrDefault(x => x.Tag is SqlInstance i && i.Server.Equals(server, StringComparison.OrdinalIgnoreCase));
                if (item != null) { item.Selected = true; _useExisting.Checked = true; }
                else { _otherServer.Text = server; _useOther.Checked = true; }
            }
            _welcomeText.Text += "\r\n\r\nKshethra Mithra is already installed on this computer. Setup will upgrade it and keep the existing database and settings.";
        }
        catch { /* a damaged state file only loses the defaults */ }
    }
}
