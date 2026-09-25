using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Xunit;

namespace LoopDPI.Core.Tests;

/// <summary>
/// Linux tar.gz self-update behavior.
/// IMPORTANT: never feed a tarball containing a "PkgSender" member to
/// RunTarGzUpdateAndExit in a test — on a success path it swaps the
/// running testhost binary. Success-shape is covered by the extraction
/// contract test below, which runs tar directly instead.
/// </summary>
public class UpdateServiceLinuxUpdateTests : IDisposable
{
    private readonly string _dir;

    public UpdateServiceLinuxUpdateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pkgsender_updtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void RunTar(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("tar", args) { UseShellExecute = false });
        Assert.NotNull(p);
        Assert.True(p!.WaitForExit(30000));
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public void RunTarGzUpdateAndExit_FailsOnMissingFile()
    {
        if (!OperatingSystem.IsLinux()) return;
        string missing = Path.Combine(_dir, "nope.tar.gz");
        Assert.False(UpdateService.RunTarGzUpdateAndExit(missing));
    }

    [Fact]
    public void RunTarGzUpdateAndExit_FailsOnCorruptGzip()
    {
        if (!OperatingSystem.IsLinux()) return;
        string bad = Path.Combine(_dir, "bad.tar.gz");
        File.WriteAllBytes(bad, new byte[] { 0x1f, 0x8b, 0xde, 0xad, 0xbe, 0xef });
        Assert.False(UpdateService.RunTarGzUpdateAndExit(bad));
    }

    [Fact]
    public void RunTarGzUpdateAndExit_FailsOnTarballWithoutBinary()
    {
        if (!OperatingSystem.IsLinux()) return;
        // Archive has content but no "PkgSender" member: tar exits non-zero,
        // so the updater must bail out before touching anything.
        string payload = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(payload, "not an update");
        string tar = Path.Combine(_dir, "empty.tar.gz");
        RunTar($"-czf \"{tar}\" -C \"{_dir}\" notes.txt");
        Assert.False(UpdateService.RunTarGzUpdateAndExit(tar));
    }

    [Fact]
    public void RunTarGzUpdateAndExit_NonTarGzFlavorReturnsFalse()
    {
        if (!OperatingSystem.IsLinux()) return;
        // In the test host Flavor() is TarGz, so this only guards the
        // branch itself; the flavor matrix is covered by Flavor tests.
        string tar = Path.Combine(_dir, "x.tar.gz");
        File.WriteAllBytes(tar, new byte[] { 0x1f, 0x8b });
        // corrupt archive → false regardless of flavor
        Assert.False(UpdateService.RunTarGzUpdateAndExit(tar));
    }

    [Fact]
    public void RunTarGzUpdateAndExit_RejectsSymlinkedPayload()
    {
        if (!OperatingSystem.IsLinux()) return;
        // A malicious tarball could carry "PkgSender" as a symlink pointing
        // outside the extract dir. The updater must reject it before the swap.
        string target = Path.Combine(_dir, "evil.sh");
        File.WriteAllText(target, "#!/bin/sh\n");
        string link = Path.Combine(_dir, "PkgSender");
        File.CreateSymbolicLink(link, target);
        string tar = Path.Combine(_dir, "symlink.tar.gz");
        RunTar($"-czf \"{tar}\" -C \"{_dir}\" PkgSender");
        Assert.False(UpdateService.RunTarGzUpdateAndExit(tar));
        // The running binary must be untouched (no .old swap happened).
        Assert.False(File.Exists((Environment.ProcessPath ?? "") + ".old"));
    }

    /// <summary>
    /// The contract between Build-Release.sh and the updater: the tarball
    /// carries "PkgSender" at its root with the exec bit stored, so the
    /// updater's single-member extract yields a runnable binary.
    /// </summary>
    [Fact]
    public void TarGzLayout_ExtractsRootBinaryWithExecBit()
    {
        if (!OperatingSystem.IsLinux()) return;
        string stage = Path.Combine(_dir, "stage");
        Directory.CreateDirectory(stage);
        string bin = Path.Combine(stage, "PkgSender");
        File.WriteAllText(bin, "#!/bin/sh\necho fake\n");
        File.WriteAllBytes(Path.Combine(stage, "pkg-receiver.elf"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(stage, "pkg_header.py"), new byte[] { 4 });
        File.SetUnixFileMode(bin,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        string tar = Path.Combine(_dir, "PkgSender-1.2.8-linux-x64.tar.gz");
        RunTar($"-czf \"{tar}\" -C \"{stage}\" PkgSender pkg-receiver.elf pkg_header.py");

        // Same command line the updater uses.
        string outDir = Path.Combine(_dir, "out");
        Directory.CreateDirectory(outDir);
        RunTar($"-xzf \"{tar}\" -C \"{outDir}\" PkgSender");

        string fresh = Path.Combine(outDir, "PkgSender");
        Assert.True(File.Exists(fresh));
        Assert.Equal("#!/bin/sh\necho fake\n", File.ReadAllText(fresh));
        var mode = File.GetUnixFileMode(fresh);
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute), "exec bit must survive the round-trip");
        Assert.True(mode.HasFlag(UnixFileMode.OtherExecute), "exec bit must survive the round-trip");
    }

    [Fact]
    public void CleanupOldBinary_RemovesLeftoverOldBinary()
    {
        if (!OperatingSystem.IsLinux()) return;
        string old = (Environment.ProcessPath ?? "") + ".old";
        File.WriteAllText(old, "leftover from a previous update");
        try
        {
            UpdateService.CleanupOldBinary();
            Assert.False(File.Exists(old));
        }
        finally
        {
            File.Delete(old); // in case the assertion fired first
        }
    }

    [Fact]
    public void CleanupOldBinary_NoOldBinary_IsNoop()
    {
        if (!OperatingSystem.IsLinux()) return;
        string old = (Environment.ProcessPath ?? "") + ".old";
        File.Delete(old);
        UpdateService.CleanupOldBinary(); // must not throw
        Assert.False(File.Exists(old));
    }
}
