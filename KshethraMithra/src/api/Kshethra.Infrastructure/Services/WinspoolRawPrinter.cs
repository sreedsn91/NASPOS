using System.ComponentModel;
using System.Runtime.InteropServices;
using Kshethra.Application.Common;
using Kshethra.Domain;

namespace Kshethra.Infrastructure.Services;

/// <summary>
/// Sends bytes to a Windows printer as a RAW spooler job, bypassing the driver's rendering (and its cut settings).
/// The printer must be installed on the server PC; the IIS app pool account needs Print permission on it (the default).
/// </summary>
public sealed class WinspoolRawPrinter : IRawPrinter
{
    public void Send(string printerName, string documentName, byte[] data)
    {
        if (!OperatingSystem.IsWindows()) throw new DomainException("Direct printing needs the server to run on Windows.", "print_failed");
        if (!OpenPrinter(printerName, out var h, IntPtr.Zero))
            throw new DomainException($"Printer '{printerName}' was not found on the server ({Error()}). Check the name in Temple › Vazhipadu Print Settings.", "print_failed");
        try
        {
            if (StartDocPrinter(h, 1, new DocInfo { DocName = documentName, DataType = "RAW" }) == 0)
                throw new DomainException($"Could not start the print job on '{printerName}' ({Error()}).", "print_failed");
            try
            {
                StartPagePrinter(h);
                if (!WritePrinter(h, data, data.Length, out var written) || written != data.Length)
                    throw new DomainException($"Sending to '{printerName}' failed ({Error()}).", "print_failed");
                EndPagePrinter(h);
            }
            finally { EndDocPrinter(h); }
        }
        finally { ClosePrinter(h); }
    }

    private static string Error() => new Win32Exception(Marshal.GetLastWin32Error()).Message;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class DocInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? DocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? DataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool ClosePrinter(IntPtr handle);
    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocPrinter(IntPtr handle, int level, [In] DocInfo docInfo);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool EndDocPrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool StartPagePrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool EndPagePrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool WritePrinter(IntPtr handle, byte[] data, int count, out int written);
}
