using System;
using System.Collections.Generic;
using LoopDPI.Core;
using Xunit;

namespace LoopDPI.Core.Tests;

public class UpdateServiceAssetTests
{
    private static UpdateService.Asset A(string name) =>
        new(name, $"https://example.invalid/{name}", 1);

    private static UpdateService.Release Rel(params string[] names) =>
        new("v1.2.8", "v1.2.8", "", new List<UpdateService.Asset>(Array.ConvertAll(names, A)));

    [Fact]
    public void Flavor_OnLinuxTestHost_IsTarGz()
    {
        if (!OperatingSystem.IsLinux()) return; // suite stays green on Windows
        // testhost runs from bin/, never /opt/PkgSender or an .AppImage
        Assert.Equal(UpdateService.LinuxFlavor.TarGz, UpdateService.Flavor());
    }

    [Fact]
    public void Flavor_OnWindows_IsNone()
    {
        if (OperatingSystem.IsLinux()) return;
        Assert.Equal(UpdateService.LinuxFlavor.None, UpdateService.Flavor());
    }

    [Fact]
    public void PickSetupAsset_OnLinux_PicksLinuxTarGz()
    {
        if (!OperatingSystem.IsLinux()) return;
        var asset = UpdateService.PickSetupAsset(Rel(
            "PkgSender-Setup-1.2.8.exe",
            "PkgSender-1.2.8-android.apk",
            "PkgSender-1.2.8-linux-x64.tar.gz"));
        Assert.NotNull(asset);
        Assert.Equal("PkgSender-1.2.8-linux-x64.tar.gz", asset!.Name);
    }

    [Fact]
    public void PickSetupAsset_OnLinux_IsCaseInsensitive()
    {
        if (!OperatingSystem.IsLinux()) return;
        var asset = UpdateService.PickSetupAsset(Rel("PkgSender-1.2.8-LINUX-x64.TAR.GZ"));
        Assert.NotNull(asset);
        Assert.Equal("PkgSender-1.2.8-LINUX-x64.TAR.GZ", asset!.Name);
    }

    [Fact]
    public void PickSetupAsset_OnLinux_ReturnsNullWithoutLinuxAsset()
    {
        if (!OperatingSystem.IsLinux()) return;
        // Windows/Android assets only — a Linux user must not be offered the Setup exe.
        Assert.Null(UpdateService.PickSetupAsset(Rel(
            "PkgSender-Setup-1.2.8.exe", "PkgSender-1.2.8-android.apk")));
    }

    [Fact]
    public void PickSetupAsset_OnLinux_IgnoresAppImageAssets()
    {
        if (!OperatingSystem.IsLinux()) return;
        // tar.gz self-update is the only in-place path; AppImage is rejected on purpose.
        Assert.Null(UpdateService.PickSetupAsset(Rel("PkgSender-1.2.8-linux-x64.AppImage")));
    }

    [Fact]
    public void PickSetupAsset_OnWindows_PicksSetupExe()
    {
        if (OperatingSystem.IsLinux()) return;
        var asset = UpdateService.PickSetupAsset(Rel(
            "PkgSender-1.2.8-android.apk", "PkgSender-Setup-1.2.8.exe"));
        Assert.NotNull(asset);
        Assert.Equal("PkgSender-Setup-1.2.8.exe", asset!.Name);
    }
}
