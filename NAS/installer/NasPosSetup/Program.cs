using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace NasPosSetup;

/// <summary>
/// Self-extracting setup: [launcher exe][payload.zip][8-byte payload length][8-byte magic "NASPOS01"].
/// No arguments: shows the setup wizard (<see cref="SetupForm"/>), which extracts the payload and runs install.ps1 -Gui.
/// /quiet [install.ps1 args]: unattended install with defaults, no window. /uninstall: runs uninstall.ps1 in a console window.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool quiet = args.Any(a => a.Equals("-Quiet", StringComparison.OrdinalIgnoreCase) || a.Equals("/quiet", StringComparison.OrdinalIgnoreCase));
        bool uninstall = args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase) || a.Equals("-Uninstall", StringComparison.OrdinalIgnoreCase));

        if (!quiet && !uninstall)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new SetupForm())
            {
                Application.Run(form);
                return form.ExitCode;
            }
        }

        var passThrough = args.Where(a => !a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase) && !a.Equals("-Uninstall", StringComparison.OrdinalIgnoreCase)
                                          && !a.Equals("/quiet", StringComparison.OrdinalIgnoreCase)).ToList();
        if (quiet && !passThrough.Contains("-Quiet", StringComparer.OrdinalIgnoreCase)) passThrough.Add("-Quiet");

        string work = Payload.NewWorkDir();
        try
        {
            Payload.Extract(work);
            var psi = Payload.PowerShell(Path.Combine(work, uninstall ? "uninstall.ps1" : "install.ps1"), passThrough, work);
            psi.CreateNoWindow = quiet;   // uninstall without /quiet asks for confirmation in its own console window
            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                return p.ExitCode;
            }
        }
        catch (Exception ex)
        {
            if (!quiet) MessageBox.Show("Setup could not start: " + ex.Message, "NAS POS Pro Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            Payload.Cleanup(work);
        }
    }
}

internal static class Payload
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("NASPOS01");

    public static string NewWorkDir() => Path.Combine(Path.GetTempPath(), "NasPosSetup_" + Guid.NewGuid().ToString("N").Substring(0, 8));

    public static void Cleanup(string work)
    {
        try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { /* temp files are cleaned by Windows later */ }
    }

    public static ProcessStartInfo PowerShell(string script, IEnumerable<string> args, string workDir)
    {
        string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem ? "System32" : "Sysnative", @"WindowsPowerShell\v1.0\powershell.exe");
        string argLine = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(script) + " " + string.Join(" ", args.Select(Quote));
        return new ProcessStartInfo(ps, argLine) { UseShellExecute = false, WorkingDirectory = workDir };
    }

    public static void Extract(string target)
    {
        string self = Process.GetCurrentProcess().MainModule.FileName;
        string zipPath = target + ".zip";
        using (var fs = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (fs.Length < 16) throw new InvalidDataException("Setup file is damaged.");
            var trailer = new byte[16];
            fs.Seek(-16, SeekOrigin.End);
            ReadExactly(fs, trailer, 16);
            if (!trailer.Skip(8).SequenceEqual(Magic)) throw new InvalidDataException("This setup has no payload. Build it with installer\\build-installer.ps1.");
            long length = BitConverter.ToInt64(trailer, 0);
            if (length <= 0 || length > fs.Length - 16) throw new InvalidDataException("Setup file is damaged.");
            fs.Seek(fs.Length - 16 - length, SeekOrigin.Begin);
            using (var zip = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
            {
                var buffer = new byte[1 << 20];
                long left = length;
                while (left > 0)
                {
                    int n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (n <= 0) throw new EndOfStreamException("Setup file is truncated.");
                    zip.Write(buffer, 0, n);
                    left -= n;
                }
            }
        }
        try { ZipFile.ExtractToDirectory(zipPath, target); }
        finally { File.Delete(zipPath); }
    }

    private static void ReadExactly(Stream s, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buffer, read, count - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
    }

    public static string Quote(string a) => a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
}
