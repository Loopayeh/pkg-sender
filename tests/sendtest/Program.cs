using System;
using System.IO;
using System.Threading.Tasks;
using LoopDPI.Core;

static class SendTest
{
    static async Task<int> Main(string[] args)
    {
        string psIp = args.Length > 0 ? args[0] : "127.0.0.1";
        Console.WriteLine("online=" + await ConsoleClient.IsOnlineAsync(psIp));

        // 5MB temp file -> exercises multiple 2MB chunks + resume + verify.
        string tmp = Path.Combine(Path.GetTempPath(), "sendtest_" + Guid.NewGuid().ToString("N") + ".bin");
        var rnd = new Random(42);
        byte[] data = new byte[5 * 1024 * 1024 + 123];
        rnd.NextBytes(data);
        await File.WriteAllBytesAsync(tmp, data);

        using var client = new ReceiverClient(psIp, timeoutSeconds: 30);
        using var mgr = new TransferManager(client, 2 * 1024 * 1024);
        mgr.ItemChanged += ti => Console.WriteLine($"item {ti.State} sent={ti.Sent}/{ti.Size} msg={ti.Message}");
        mgr.ProgressChanged += p => Console.WriteLine($"progress {p.DoneBytes}/{p.TotalBytes} {p.Percent:F1}% {p.CurrentFile}");
        mgr.EnqueueFile(tmp, "/data/homebrew/SENDTEST/file.bin");
        await mgr.RunAsync();
        foreach (var it in mgr.Items)
            Console.WriteLine($"FINAL {it.State} {it.Message}");
        File.Delete(tmp);
        return 0;
    }
}
