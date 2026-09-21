using System.IO;
using System.Printing;
using System.Runtime.InteropServices;
using System.Text;
using POS.Application.Abstractions;
using POS.Application.Models;

namespace POS.Wpf.Services;

public sealed class EscPosReceiptPrinter : IReceiptPrinter
{
    public void Print(ReceiptDto receipt)
    {
        var bytes = BuildEscPos(receipt);

        // Try raw ESC/POS printing; fall back to a text file if anything goes wrong.
        var printed = false;
        try
        {
            var printerName = GetDefaultPrinterName();
            if (!string.IsNullOrEmpty(printerName))
                printed = TryRawPrint(printerName, bytes);
        }
        catch { /* ignore – fall through to file fallback */ }

        if (!printed)
            SaveFallbackFile(bytes);
    }

    // ── Receipt builder ──────────────────────────────────────────────────────

    private static byte[] BuildEscPos(ReceiptDto r)
    {
        var sb = new StringBuilder();
        sb.Append('\x1B').Append('@');                       // ESC @ — init
        sb.Append('\x1B').Append('a').Append('\x01');        // centre
        sb.AppendLine(r.StoreName);
        sb.Append('\x1B').Append('a').Append('\x00');        // left
        sb.AppendLine($"Invoice: {r.InvoiceNumber}");
        sb.AppendLine(r.PaidAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        sb.AppendLine(new string('-', 32));
        foreach (var line in r.Lines)
            sb.AppendLine($"{line.Name} x{line.Quantity:N2} @ {line.UnitPrice:N2} = {line.LineTotal:N2}");
        sb.AppendLine(new string('-', 32));
        sb.AppendLine($"Total ({r.Currency}): {r.Total:N2}");
        sb.AppendLine($"Cash:   {r.CashTendered:N2}");
        sb.AppendLine($"Change: {r.Change:N2}");
        if (!string.IsNullOrWhiteSpace(r.Notes))
        {
            sb.AppendLine(new string('-', 32));
            sb.AppendLine($"Note: {r.Notes}");
        }
        sb.AppendLine();
        sb.AppendLine("Thank you!");
        sb.Append('\x1D').Append('V').Append('\x41').Append('\x03'); // cut
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void SaveFallbackFile(byte[] bytes)
    {
        try
        {
            var dir  = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "last-receipt.txt");
            File.WriteAllText(path, Encoding.UTF8.GetString(bytes), Encoding.UTF8);
        }
        catch { /* best-effort */ }
    }

    // ── Default printer ──────────────────────────────────────────────────────

    private static string? GetDefaultPrinterName()
    {
        try { return LocalPrintServer.GetDefaultPrintQueue()?.FullName; }
        catch { return null; }
    }

    // ── Raw print via Win32 ──────────────────────────────────────────────────

    private static bool TryRawPrint(string printerName, byte[] data)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero) || hPrinter == IntPtr.Zero)
            return false;

        var docStarted  = false;
        var pageStarted = false;
        try
        {
            var di = new DOCINFOA { pDocName = "POS Receipt", pDataType = "RAW" };
            docStarted = StartDocPrinter(hPrinter, 1, di);
            if (!docStarted) return false;

            pageStarted = StartPagePrinter(hPrinter);
            if (!pageStarted) return false;

            var writeOk = WritePrinter(hPrinter, data, data.Length, out var written);
            return writeOk && written == data.Length;
        }
        finally
        {
            if (pageStarted) EndPagePrinter(hPrinter);
            if (docStarted) EndDocPrinter(hPrinter);
            ClosePrinter(hPrinter);
        }
    }

    // ── P/Invoke declarations ─────────────────────────────────────────────────
    // ExactSpelling must NOT be true: winspool exports "OpenPrinterW"/"OpenPrinterA",
    // not a bare "OpenPrinter". Without ExactSpelling the runtime appends W/A automatically.

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private sealed class DOCINFOA
    {
        [MarshalAs(UnmanagedType.LPStr)] public string pDocName   = "";
        [MarshalAs(UnmanagedType.LPStr)] public string pOutputFile = "";
        [MarshalAs(UnmanagedType.LPStr)] public string pDataType  = "";
    }

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In] DOCINFOA di);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, byte[] pBytes, int dwCount, out int dwWritten);
}
