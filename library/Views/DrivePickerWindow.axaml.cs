using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ReactiveUI;

namespace PkgSender.Views;

public sealed class DriveChoice : ReactiveObject
{
    public string Root { get; init; } = "";
    public string Label { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool IsReady { get; init; }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => this.RaiseAndSetIfChanged(ref _isSelected, value); }
}

public partial class DrivePickerWindow : Window
{
    public ObservableCollection<DriveChoice> Drives { get; } = new();

    public DrivePickerWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = this;
        this.FindControl<Button>("BtnOk").Click += (_, _) =>
            Close(Drives.Where(d => d.IsSelected).Select(d => d.Root).ToList());
        this.FindControl<Button>("BtnCancel").Click += (_, _) => Close(null);
    }

    public DrivePickerWindow(List<string> currentRoots) : this()
    {
        var current = new HashSet<string>(currentRoots, StringComparer.OrdinalIgnoreCase);
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            return;
        }
        foreach (var d in drives)
        {
            string root = d.Name; // e.g. "E:\"
            bool ready = false;
            string label = root, detail = "not ready";
            try
            {
                ready = d.IsReady;
                if (ready)
                {
                    string vol = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "" : $" — {d.VolumeLabel}";
                    label = $"{root}{vol}";
                    detail = $"{d.DriveType} • {Program.FormatSize(d.AvailableFreeSpace)} free of {Program.FormatSize(d.TotalSize)}";
                }
                else
                {
                    label = root;
                    detail = $"{d.DriveType} • not ready";
                }
            }
            catch
            {
            }
            Drives.Add(new DriveChoice
            {
                Root = root,
                Label = label,
                Detail = detail,
                IsReady = ready,
                IsSelected = ready && (current.Contains(root) || current.Contains(root.TrimEnd('\\'))),
            });
        }
    }
}
