using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LoopDPI.Core;

namespace PkgSender.Views;

public partial class UpdateDialog : Window
{
    private readonly UpdateService.Release _info;

    public UpdateDialog(UpdateService.Release info)
    {
        _info = info;
        AvaloniaXamlLoader.Load(this);
        this.FindControl<TextBlock>("VersionText").Text =
            $"{info.Name}  (you have {UpdateService.AppVersion})";
        string[] lines = (info.Body ?? "").Split('\n');
        this.FindControl<TextBox>("NotesBox").Text =
            string.Join('\n', lines.Length > 12 ? lines[..12] : lines);
        this.FindControl<Button>("LaterButton").Click += (_, _) => Close();
        this.FindControl<Button>("InstallButton").Click += async (_, _) => await DownloadAndInstallAsync();
    }

    private async Task DownloadAndInstallAsync()
    {
        var prog = this.FindControl<TextBlock>("ProgressText");
        var btn = this.FindControl<Button>("InstallButton");
        var asset = UpdateService.PickSetupAsset(_info);
        if (asset == null)
        {
            prog.Text = "No installer found in this release.";
            return;
        }
        btn.IsEnabled = false;
        prog.Text = $"Downloading {asset.Name}…";
        string tmp = Path.Combine(Path.GetTempPath(), "pkgsender_update_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        string dest = Path.Combine(tmp, asset.Name);
        try
        {
            await UpdateService.DownloadAsync(asset.Url, dest, (got, total) =>
                Dispatcher.UIThread.Post(() =>
                    prog.Text = total > 0 ? $"Downloading… {got * 100 / total}%"
                                          : $"Downloading… {got / 1024 / 1024} MB"));
        }
        catch (Exception ex)
        {
            prog.Text = "Download failed: " + ex.Message;
            btn.IsEnabled = true;
            return;
        }
        prog.Text = "Installing — the app will close…";
        await Task.Delay(500);
        if (OperatingSystem.IsLinux())
        {
            prog.Text = "Updating — the app will restart…";
            if (UpdateService.RunTarGzUpdateAndExit(dest))
                Environment.Exit(0);
            prog.Text = "Update failed (only tar.gz installs can self-update; use your package manager).";
            btn.IsEnabled = true;
            return;
        }
        if (UpdateService.RunSetupAndExit(dest))
            Environment.Exit(0);
        prog.Text = "Could not launch the installer.";
        btn.IsEnabled = true;
    }
}
