using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LoopDPI.Core;

/// <summary>
/// Minimal LAN file server with byte-range (206) support, so the PS5
/// downloader can resume/segment. Raw sockets: no admin URL reservation.
/// Serves one file (/pkg) or a set of files (/pkg/{id}).
/// Reports served bytes for progress bars.
/// </summary>
/// <summary>One library row for the console browser catalog (PKG only).</summary>
public sealed class CatalogEntry
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string TitleId { get; init; } = "";
    public string Version { get; init; } = "";
    public long Size { get; init; }
    public string SizeText { get; init; } = "";
    public string Role { get; init; } = "Game"; // Game | Patch | DLC | Image
    public string FamilyKey { get; init; } = "";
    public string Platform { get; init; } = "";
    public string Format { get; init; } = "pkg"; // pkg | exfat | ffpfsc | ffpkg
    public string File { get; init; } = ""; // basename for copy-to-console
    public bool HasIcon { get; init; }
}

/// <summary>
/// Seekable byte source for direct (no-copy) serving, e.g. an Android
/// SAF document served via dup'd fd + lseek. OpenAt must return a
/// stream positioned at offset, independent per call (thread-safe).
/// </summary>
public interface IRangeSource
{
    long Length { get; }
    Stream OpenAt(long offset);
}

public sealed class RangeFileServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly IReadOnlyDictionary<string, string> _files;
    private readonly ConcurrentDictionary<string, IRangeSource> _sources = new();
    private readonly long _singleSize;
    private CancellationTokenSource? _cts;
    private long _served;
    private readonly ConcurrentDictionary<string, long> _servedById = new();
    private readonly ConcurrentDictionary<string, byte> _revoked = new();
    private readonly ConcurrentDictionary<string, byte[]> _icons = new();

    public int Port { get; }
    public long Served => Interlocked.Read(ref _served);
    /// <summary>Bytes served for one registered id (per-file progress).</summary>
    public long ServedFor(string id) => _servedById.TryGetValue(id, out var v) ? v : 0;
    /// <summary>
    /// Stop serving one id (404 from now on). The console's in-flight
    /// download of it errors out instead of continuing silently.
    /// </summary>
    public void Revoke(string id) => _revoked[id] = 0;
    /// <summary>Serve the id again (undo Revoke, e.g. for resume).</summary>
    public void Unrevoke(string id) => _revoked.TryRemove(id, out _);
    /// <summary>Zero the per-file served counter (fresh push of the same id).</summary>
    public void ResetServed(string id) => _servedById[id] = 0;
    public event Action<long, long>? Progress;
    public event Action<string>? FileRequested;

    public RangeFileServer(string filePath, int port = 9898)
        : this(new Dictionary<string, string> { ["pkg"] = filePath }, port)
    {
        _singleSize = new FileInfo(filePath).Length;
    }

    public RangeFileServer(IReadOnlyDictionary<string, string> files, int port = 9898)
    {
        _files = files;
        Port = port;
        _listener = new TcpListener(IPAddress.Any, port);
    }

    public long FileSize => _singleSize;
    public string UrlFor(string host) => $"http://{host}:{Port}/pkg";
    public string UrlFor(string host, string id) => $"http://{host}:{Port}/pkg/{Uri.EscapeDataString(id)}";
    /// <summary>Serve one game's in-memory cover PNG to the console installer UI.</summary>
    public void RegisterIcon(string id, byte[] png) => _icons[id] = png;
    public string IconUrlFor(string host, string id) => $"http://{host}:{Port}/icon/{Uri.EscapeDataString(id)}";
    /// <summary>Library rows served at GET /catalog (set by Publish library).</summary>
    public Func<IReadOnlyList<CatalogEntry>>? CatalogProvider { get; set; }
    public string CatalogUrlFor(string host) => $"http://{host}:{Port}/catalog";
    /// <summary>PS4 GoldHEN JSON manifests (/json/{id}.json). Keyed by id.</summary>
    private readonly ConcurrentDictionary<string, byte[]> _manifests = new();
    public void RegisterManifest(string id, string json) =>
        _manifests[id] = Encoding.UTF8.GetBytes(json);
    /// <summary>Serve id from a seekable source instead of a file (direct mode).</summary>
    public void RegisterSource(string id, IRangeSource source) => _sources[id] = source;
    public void UnregisterSource(string id) => _sources.TryRemove(id, out _);
    public string ManifestUrlFor(string host, string id) =>
        $"http://{host}:{Port}/json/{Uri.EscapeDataString(id)}.json";
    /// <summary>Optional sink for every HTTP request line (diagnostics).</summary>
    public Action<string>? RequestLog { get; set; }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start(32);
        Task.Run(() => AcceptLoop(_cts.Token));
    }

    /// <summary>Total bytes across all registered files and sources (queue progress).</summary>
    public long TotalBytes()
    {
        long total = 0;
        foreach (var f in _files.Values)
        {
            try
            {
                total += new FileInfo(f).Length;
            }
            catch
            {
            }
        }
        foreach (var s in _sources.Values)
        {
            try
            {
                total += s.Length;
            }
            catch
            {
            }
        }
        return total;
    }

    private static FileStream OpenFileAt(string path, long start)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024 * 1024, FileOptions.SequentialScan);
        fs.Seek(start, SeekOrigin.Begin);
        return fs;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient cl;
            try
            {
                cl = await _listener.AcceptTcpClientAsync(ct);
            }
            catch
            {
                return;
            }
            _ = Task.Run(() => Handle(cl, ct));
        }
    }

    private async Task Handle(TcpClient cl, CancellationToken ct)
    {
        // Bulk-send tuning: big kernel send buffer so the PS5 can pull at
        // line rate instead of a few MB/s (same class of fix as zftpd's).
        try
        {
            cl.SendBufferSize = 4 * 1024 * 1024;
            cl.NoDelay = false;
        }
        catch
        {
        }
        using (cl)
        using (var ns = cl.GetStream())
        {
            var req = new byte[16384];
            var sb = new StringBuilder();
            try
            {
                int n;
                while (!sb.ToString().Contains("\r\n\r\n"))
                {
                    n = await ns.ReadAsync(req, ct);
                    if (n == 0)
                        return;
                    sb.Append(Encoding.ASCII.GetString(req, 0, n));
                    if (sb.Length > 32768)
                        return;
                }
            }
            catch
            {
                return;
            }

            string header = sb.ToString();
            string first = header[..header.IndexOf("\r\n")];
            // GET/HEAD /pkg or /pkg/{id} (Sony appends ?product=..&.., ignored)
            var reqParts = first.Split(' ');
            string method = reqParts.Length > 0 ? reqParts[0].ToUpperInvariant() : "";
            string rawTarget = reqParts.Length > 1 ? reqParts[1] : "";
            if (method != "GET" && method != "HEAD")
            {
                await WriteRaw(ns, "HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct);
                return;
            }
            bool isHead = method == "HEAD";
            string noQuery = rawTarget.Split('?')[0];
            string clientIp = "unknown";
            try { clientIp = (cl.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown"; } catch { }
            void Log(string status) { try { RequestLog?.Invoke($"{clientIp} {method} {noQuery} -> {status}"); } catch { } }
            Log("in");
            // /catalog: JSON library for the console browser (PKG only).
            if (noQuery.Equals("/catalog", StringComparison.OrdinalIgnoreCase))
            {
                string json = "[]";
                try
                {
                    var rows = CatalogProvider?.Invoke();
                    if (rows != null)
                    {
                        var sb2 = new StringBuilder("[");
                        bool firstRow = true;
                        foreach (var r in rows)
                        {
                            if (!firstRow)
                                sb2.Append(',');
                            firstRow = false;
                            sb2.Append("{\"id\":\"").Append(JsonEscape(r.Id)).Append('"');
                            sb2.Append(",\"title\":\"").Append(JsonEscape(r.Title)).Append('"');
                            sb2.Append(",\"titleId\":\"").Append(JsonEscape(r.TitleId)).Append('"');
                            sb2.Append(",\"version\":\"").Append(JsonEscape(r.Version)).Append('"');
                            sb2.Append(",\"size\":").Append(r.Size);
                            sb2.Append(",\"sizeText\":\"").Append(JsonEscape(r.SizeText)).Append('"');
                            sb2.Append(",\"role\":\"").Append(JsonEscape(r.Role)).Append('"');
                            sb2.Append(",\"familyKey\":\"").Append(JsonEscape(r.FamilyKey)).Append('"');
                            sb2.Append(",\"platform\":\"").Append(JsonEscape(r.Platform)).Append('"');
                            sb2.Append(",\"format\":\"").Append(JsonEscape(r.Format)).Append('"');
                            sb2.Append(",\"file\":\"").Append(JsonEscape(r.File)).Append('"');
                            sb2.Append(",\"hasIcon\":").Append(r.HasIcon ? "true" : "false");
                            sb2.Append('}');
                        }
                        sb2.Append(']');
                        json = sb2.ToString();
                    }
                }
                catch
                {
                }
                var jb = Encoding.UTF8.GetBytes(json);
                await WriteRaw(ns, $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nAccess-Control-Allow-Origin: *\r\nContent-Length: {jb.Length}\r\nConnection: close\r\n\r\n", ct);
                if (!isHead)
                {
                    try
                    {
                        await ns.WriteAsync(jb, ct);
                    }
                    catch
                    {
                    }
                }
                return;
            }
            // /icon/{id}: small in-memory cover PNG for the console installer UI.
            if (noQuery.StartsWith("/icon/", StringComparison.OrdinalIgnoreCase))
            {
                string iconId = Uri.UnescapeDataString(noQuery["/icon/".Length..]);
                if (!_icons.TryGetValue(iconId, out var png) || png.Length == 0 ||
                    _revoked.ContainsKey(iconId))
                {
                    Log($"icon 404 ({iconId})");
                    await WriteRaw(ns, "HTTP/1.1 404 Not Found\r\nContent-Length: 9\r\nConnection: close\r\n\r\nnot found", ct);
                    return;
                }
                FileRequested?.Invoke(iconId);
                Log($"icon 200 ({iconId}, {png.Length} bytes)");
                await WriteRaw(ns, $"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nAccess-Control-Allow-Origin: *\r\nContent-Length: {png.Length}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\n", ct);
                if (!isHead)
                {
                    try
                    {
                        await ns.WriteAsync(png, ct);
                    }
                    catch
                    {
                    }
                }
                return;
            }
            // /json/{id}.json: PS4 GoldHEN PKG manifest (pieces list).
            if (noQuery.StartsWith("/json/", StringComparison.OrdinalIgnoreCase) &&
                noQuery.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                string mid = Uri.UnescapeDataString(noQuery["/json/".Length..^".json".Length]);
                if (_manifests.TryGetValue(mid, out var mjson) && mjson.Length > 0)
                {
                    Log("manifest 200");
                    await WriteRaw(ns, $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {mjson.Length}\r\nConnection: close\r\n\r\n", ct);
                    if (!isHead)
                    {
                        try { await ns.WriteAsync(mjson, ct); } catch { }
                    }
                    return;
                }
                Log("manifest 404");
                await WriteRaw(ns, "HTTP/1.1 404 Not Found\r\nContent-Length: 9\r\nConnection: close\r\n\r\nnot found", ct);
                return;
            }
            string id = "pkg";
            if (noQuery.StartsWith("/pkg/", StringComparison.OrdinalIgnoreCase))
                id = Uri.UnescapeDataString(noQuery["/pkg/".Length..]);
            else if (!noQuery.Equals("/pkg", StringComparison.OrdinalIgnoreCase))
            {
                Log("404");
                await WriteRaw(ns, "HTTP/1.1 404 Not Found\r\nContent-Length: 9\r\nConnection: close\r\n\r\nnot found", ct);
                return;
            }

            IRangeSource? src = null;
            string? path = null;
            if (!_revoked.ContainsKey(id))
            {
                if (_sources.TryGetValue(id, out var s))
                    src = s;
                else if (_files.TryGetValue(id, out var p) && File.Exists(p))
                    path = p;
            }
            if (src == null && path == null)
            {
                Log("pkg 404");
                await WriteRaw(ns, "HTTP/1.1 404 Not Found\r\nContent-Length: 9\r\nConnection: close\r\n\r\nnot found", ct);
                return;
            }
            FileRequested?.Invoke(id);

            long size;
            try
            {
                size = src != null ? src.Length : new FileInfo(path!).Length;
            }
            catch
            {
                return;
            }

            long start = 0, end = size - 1;
            bool partial = false;
            bool unsatisfiable = false;
            foreach (var line in header.Split("\r\n"))
            {
                if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                    continue;
                var spec = line[6..].Trim();
                if (!spec.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                    continue;
                string range = spec[6..].Trim();
                if (range.Contains(',')) // multipart ranges not supported
                    continue;
                var parts = range.Split('-');
                if (parts.Length != 2)
                    continue;
                if (parts[0].Length == 0)
                {
                    // Suffix: last N bytes.
                    if (long.TryParse(parts[1], out var suffix) && suffix > 0)
                    {
                        start = Math.Max(0, size - suffix);
                        end = size - 1;
                        partial = true;
                    }
                }
                else if (long.TryParse(parts[0], out var s))
                {
                    if (s >= size)
                    {
                        unsatisfiable = true;
                        break;
                    }
                    start = s;
                    end = size - 1; // open-ended by default
                    if (parts[1].Length > 0 && long.TryParse(parts[1], out var e))
                        end = Math.Min(e, size - 1);
                    if (end < start)
                    {
                        unsatisfiable = true;
                        break;
                    }
                    partial = true;
                }
            }

            if (unsatisfiable)
            {
                await WriteRaw(ns, $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{size}\r\nContent-Length: 0\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\n", ct);
                return;
            }

            long length = end - start + 1;
            Log(partial ? "pkg 206" : "pkg 200");
            var h = new StringBuilder();
            h.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            if (partial)
                h.Append($"Content-Range: bytes {start}-{end}/{size}\r\n");
            h.Append("Content-Type: application/octet-stream\r\n");
            h.Append($"Content-Length: {length}\r\n");
            h.Append("Accept-Ranges: bytes\r\nConnection: close\r\n\r\n");
            await WriteRaw(ns, h.ToString(), ct);

            if (isHead)
                return; // headers only, no body

            try
            {
                using Stream fs = src != null ? src.OpenAt(start) : OpenFileAt(path!, start);
                var buf = new byte[4 * 1024 * 1024];
                while (length > 0 && !ct.IsCancellationRequested)
                {
                    int want = (int)Math.Min(buf.Length, length);
                    int got = await fs.ReadAsync(buf.AsMemory(0, want), ct);
                    if (got == 0)
                        break;
                    await ns.WriteAsync(buf.AsMemory(0, got), ct);
                    length -= got;
                    Progress?.Invoke(Interlocked.Add(ref _served, got), size);
                    _servedById.AddOrUpdate(id, got, (_, v) => v + got);
                }
            }
            catch
            {
            }
        }
    }

    private static string JsonEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");

    private static async Task WriteRaw(NetworkStream ns, string s, CancellationToken ct)
    {
        try
        {
            var b = Encoding.ASCII.GetBytes(s);
            await ns.WriteAsync(b, ct);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener.Stop();
    }
}
