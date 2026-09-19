using System;
using System.IO;
using System.Text.Json;

namespace LoopDPI.Core;

public sealed class AppSettings
{
    public string PsIp { get; set; } = "192.168.1.105";
    public string PcIp { get; set; } = "192.168.1.100";
    public string RemoteDir { get; set; } = "/data/homebrew";
    public int ChunkSize { get; set; } = 2 * 1024 * 1024;
    public bool UpdateCheck { get; set; } = true;
    public bool AboutShown { get; set; } = false;
    public bool Compact { get; set; } = false;

    public AppSettings()
    {
    }

    public AppSettings(string psIp, string pcIp)
    {
        PsIp = psIp;
        PcIp = pcIp;
    }

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LoopDPI-Sender", "settings.json");

    public static AppSettings Load()
    {
        var s = new AppSettings();
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (loaded == null)
                    return s;
                if (!string.IsNullOrWhiteSpace(loaded.PsIp))
                    s.PsIp = loaded.PsIp.Trim();
                if (!string.IsNullOrWhiteSpace(loaded.PcIp))
                    s.PcIp = loaded.PcIp.Trim();
                if (!string.IsNullOrWhiteSpace(loaded.RemoteDir))
                    s.RemoteDir = loaded.RemoteDir.Trim();
                if (loaded.ChunkSize >= 64 * 1024 && loaded.ChunkSize <= 64 * 1024 * 1024)
                    s.ChunkSize = loaded.ChunkSize;
                s.UpdateCheck = loaded.UpdateCheck;
                s.AboutShown = loaded.AboutShown;
                s.Compact = loaded.Compact;
                return s;
            }
        }
        catch
        {
        }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch
        {
        }
    }
}
