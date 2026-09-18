using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using LoopDPI.Core;

static class SrvTest
{
    static async Task<int> Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : Path.GetTempPath();
        var files = new Dictionary<string, string>();
        for (int i = 0; i < 2; i++)
        {
            string p = Path.Combine(dir, $"srvtest_{i}.pkg");
            var rnd = new Random(i);
            byte[] b = new byte[300000 + i];
            rnd.NextBytes(b);
            await File.WriteAllBytesAsync(p, b);
            files[i.ToString()] = p;
        }
        using var srv = new RangeFileServer(files, port: 19898);
        srv.Start();
        Console.WriteLine("SERVING " + srv.UrlFor("127.0.0.1", "0"));
        Console.WriteLine("TOTAL " + srv.TotalBytes());
        await Task.Delay(60000);
        return 0;
    }
}
