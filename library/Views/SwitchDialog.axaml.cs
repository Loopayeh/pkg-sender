using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PkgSender.Views;

/// <summary>True = switch to the discovered address.</summary>
public partial class SwitchDialog : Window
{
    public bool Switch { get; private set; }

    public SwitchDialog(string savedIp, string foundIp, string source)
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<TextBlock>("DetailText").Text =
            $"Saved: {savedIp} (no receiver)\nFound: {foundIp} ({source}).";
        this.FindControl<Button>("KeepButton").Click += (_, _) => Close();
        this.FindControl<Button>("SwitchButton").Click += (_, _) => { Switch = true; Close(); };
    }
}
