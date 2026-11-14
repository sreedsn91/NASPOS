using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace NasPosSetup;

/// <summary>
/// Self-extracting setup: [launcher exe][payload.zip][8-byte payload length][8-byte magic "NASPOS01"].
/// Extracts the payload to %TEMP% and runs install.ps1 (or uninstall.ps1 with /uninstall), passing the remaining arguments through.
/// </summary>
internal static class Program
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("NASPOS01");

    private static int Main(string[] args)
    {
        Console.Title = "NAS POS Pro Setup";
        bool quiet = args.Any(a => a.Equals("-Quiet", StringComparison.OrdinalIgnoreCase) || a.Equals("/quiet", StringComparison.OrdinalIgnoreCase));
        bool uninstall = args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase) || a.Equals("-Uninstall", StringComparison.OrdinalIgnoreCase));
        var passThrough = args.Where(a => !a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase) && !a.Equals("-Uninstall", StringComparison.OrdinalIgnoreCase)
                                          && !a.Equals("/quiet", StringComparison.OrdinalIgnoreCase)).ToList();
        if (quiet && !passThrough.Contains("-Quiet", StringComparer.OrdinalIgnoreCase)) passThrough.Add("-Quiet");

        string work = Path.Combine(Path.GetTempPath(), "NasPosSetup_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        int code = 1;
        try
        {
            Console.WriteLine("NAS POS Pro Setup");
            Console.WriteLine("Extracting setup files...");
            Extract(work);

            string script = Path.Combine(work, uninstall ? "uninstall.ps1" : "install.ps1");
            string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem ? "System32" : "Sysnative", @"WindowsPowerShell\v1.0\powershell.exe");
            string argLine = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(script) + " " + string.Join(" ", passThrough.Select(Quote));
            var psi = new ProcessStartInfo(ps, argLine) { UseShellExecute = false, WorkingDirectory = work };
            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                code = p.ExitCode;
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Setup could not start: " + ex.Message);
            Console.ResetColor();
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { /* temp files are cleaned by Windows later */ }
        }

        if (!quiet)
        {
            Console.WriteLine();
            Console.WriteLine("Press any key to close...");
            try { Console.ReadKey(true); } catch (InvalidOperationException) { /* no console input */ }
        }
        return code;
    }

    private static void Extract(string target)
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

    private static string Quote(string a) => a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
}
