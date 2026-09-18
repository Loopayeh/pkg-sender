using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LoopDPI.Core;

public enum TransferState
{
    Queued,
    Active,
    Paused,
    Done,
    Skipped,
    Failed,
    Cancelled,
}

/// <summary>One file inside the transfer queue.</summary>
public sealed class TransferItem
{
    public string LocalPath { get; init; } = "";
    public string RemotePath { get; init; } = "";
    public long Size { get; init; }
    public long Sent { get; set; }
    public TransferState State { get; set; } = TransferState.Queued;
    public string Message { get; set; } = "";
    public double Percent => Size <= 0 ? 100 : Math.Min(100, Sent * 100.0 / Size);
}

/// <summary>Aggregate progress snapshot for UI binding.</summary>
public sealed record TransferProgress(
    long TotalBytes, long DoneBytes, double Percent,
    double BytesPerSec, string CurrentFile, int FilesDone, int FilesTotal);

/// <summary>
/// Pushes files/folders to the pkg-receiver payload (/data/homebrew).
/// Chunked streaming (no full-file RAM), resume via stat offset,
/// retry with backoff, pause/cancel, skip-if-same-size, size verify.
/// </summary>
public sealed class TransferManager : IDisposable
{
    private const int MaxRetries = 3;

    private readonly ReceiverClient _client;
    private readonly int _chunkSize;
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _unpaused = new(true);

    private readonly List<TransferItem> _items = new();
    private readonly Stopwatch _clock = new();
    private long _doneBytes;
    private long _totalBytes;

    public IReadOnlyList<TransferItem> Items => _items;
    public bool SkipIfSameSize { get; set; } = true;
    public event Action<TransferProgress>? ProgressChanged;
    public event Action<TransferItem>? ItemChanged;

    public TransferManager(ReceiverClient client, int chunkSize)
    {
        _client = client;
        _chunkSize = Math.Clamp(chunkSize, 64 * 1024, 64 * 1024 * 1024);
    }

    public void EnqueueFile(string localPath, string remotePath)
    {
        long size;
        try
        {
            size = new FileInfo(localPath).Length;
        }
        catch
        {
            return;
        }
        _items.Add(new TransferItem { LocalPath = localPath, RemotePath = remotePath, Size = size });
        _totalBytes += size;
    }

    /// <summary>Recursively enqueue a folder, preserving relative structure under remoteDir.</summary>
    public void EnqueueFolder(string localDir, string remoteDir)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(localDir, "*", SearchOption.AllDirectories);
        }
        catch
        {
            return;
        }
        foreach (var f in files)
        {
            string rel = Path.GetRelativePath(localDir, f).Replace('\\', '/');
            EnqueueFile(f, remoteDir.TrimEnd('/') + "/" + rel);
        }
    }

    public void Pause() => _unpaused.Reset();
    public void Resume() => _unpaused.Set();
    public void Cancel() => _cts.Cancel();

    private void WaitIfPaused(CancellationToken ct)
    {
        while (!_unpaused.IsSet)
        {
            ct.ThrowIfCancellationRequested();
            _unpaused.Wait(200, ct);
        }
    }

    public async Task RunAsync()
    {
        var ct = _cts.Token;
        _clock.Restart();
        int filesDone = 0;
        foreach (var item in _items)
        {
            if (ct.IsCancellationRequested)
            {
                item.State = TransferState.Cancelled;
                item.Message = "cancelled";
                ItemChanged?.Invoke(item);
                continue;
            }
            await SendOneAsync(item, ct);
            if (item.State == TransferState.Done || item.State == TransferState.Skipped)
                filesDone++;
            Report(item, filesDone);
        }
        _clock.Stop();
        Report(null, filesDone);
    }

    private void Report(TransferItem? current, int filesDone)
    {
        double elapsed = Math.Max(0.001, _clock.Elapsed.TotalSeconds);
        ProgressChanged?.Invoke(new TransferProgress(
            _totalBytes, _doneBytes,
            _totalBytes <= 0 ? 100 : Math.Min(100, _doneBytes * 100.0 / _totalBytes),
            _doneBytes / elapsed,
            current?.RemotePath ?? "", filesDone, _items.Count));
    }

    private async Task SendOneAsync(TransferItem item, CancellationToken ct)
    {
        item.State = TransferState.Active;
        ItemChanged?.Invoke(item);

        // Ensure remote parent dir exists.
        string? dir = RemoteDirOf(item.RemotePath);
        if (dir != null)
        {
            var (ok, reply) = await _client.MkdirAsync(dir, ct);
            if (!ok && !reply.Contains("pair required"))
            {
                // mkdir failure is non-fatal: dir may already exist.
            }
            else if (!ok)
            {
                Fail(item, reply);
                return;
            }
        }

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                WaitIfPaused(ct);
                ct.ThrowIfCancellationRequested();

                // Resume point from console.
                long offset = 0;
                var (sok, exists, size, sreply) = await _client.StatAsync(item.RemotePath, ct);
                if (!sok)
                {
                    if (sreply.Contains("pair required"))
                    {
                        Fail(item, sreply);
                        return;
                    }
                    // stat failed (payload without file endpoints?) — start at 0.
                }
                else if (exists)
                {
                    if (SkipIfSameSize && size == item.Size && item.Size > 0)
                    {
                        item.Sent = item.Size;
                        item.State = TransferState.Skipped;
                        item.Message = "already on console";
                        _doneBytes += item.Size;
                        ItemChanged?.Invoke(item);
                        return;
                    }
                    if (size > 0 && size < item.Size)
                        offset = size; // resume partial
                    else if (size > item.Size)
                        offset = 0; // remote bigger — overwrite from scratch
                }

                using var fs = new FileStream(item.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.Seek(offset, SeekOrigin.Begin);
                var buf = new byte[_chunkSize];
                long sent = offset;
                item.Sent = sent;
                while (sent < item.Size)
                {
                    WaitIfPaused(ct);
                    ct.ThrowIfCancellationRequested();
                    int want = (int)Math.Min(buf.Length, item.Size - sent);
                    int got = await fs.ReadAsync(buf.AsMemory(0, want), ct);
                    if (got == 0)
                        break;
                    var (wok, wreply) = await _client.WriteChunkAsync(item.RemotePath, sent, buf, got, ct);
                    if (!wok)
                    {
                        if (wreply.Contains("pair required"))
                        {
                            Fail(item, wreply);
                            return;
                        }
                        throw new IOException("write failed: " + wreply);
                    }
                    sent += got;
                    item.Sent = sent;
                    _doneBytes += got;
                    ItemChanged?.Invoke(item);
                }

                var (dok, dreply) = await _client.DoneAsync(item.RemotePath, item.Size, ct);
                if (!dok)
                    throw new IOException("done failed: " + dreply);

                // Verify final size on console.
                var (vok, vexists, vsize, _) = await _client.StatAsync(item.RemotePath, ct);
                if (vok && vexists && vsize != item.Size)
                    throw new IOException($"verify failed: console has {vsize}, expected {item.Size}");

                item.State = TransferState.Done;
                item.Message = offset > 0 ? "resumed + done" : "done";
                ItemChanged?.Invoke(item);
                return;
            }
            catch (OperationCanceledException)
            {
                item.State = TransferState.Cancelled;
                item.Message = "cancelled";
                ItemChanged?.Invoke(item);
                return;
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                item.Message = $"retry {attempt}/{MaxRetries}: {Short(ex.Message)}";
                ItemChanged?.Invoke(item);
                try
                {
                    await Task.Delay(1000 * attempt, ct);
                }
                catch
                {
                    item.State = TransferState.Cancelled;
                    item.Message = "cancelled";
                    ItemChanged?.Invoke(item);
                    return;
                }
            }
            catch (Exception ex)
            {
                Fail(item, ex.Message);
                return;
            }
        }
    }

    private static void Fail(TransferItem item, string reply)
    {
        item.State = TransferState.Failed;
        item.Message = Short(reply);
    }

    private static string Short(string s) => s.Length > 80 ? s[..80] : s;

    private static string? RemoteDirOf(string remotePath)
    {
        int i = remotePath.LastIndexOf('/');
        return i > 0 ? remotePath[..i] : null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _unpaused.Dispose();
        _client.Dispose();
    }
}
