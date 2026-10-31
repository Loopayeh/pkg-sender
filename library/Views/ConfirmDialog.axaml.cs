using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PkgSender.Views;

/// <summary>Small yes/no confirmation (same look as SwitchDialog).</summary>
public partial class ConfirmDialog : Window
{
    public bool Confirmed { get; private set; }

    public ConfirmDialog(string title, string message, string okLabel = "OK")
    {
        AvaloniaXamlLoader.Load(this);
        Title = title;
        this.FindControl<TextBlock>("TitleText").Text = title;
        this.FindControl<TextBlock>("MessageText").Text = message;
        this.FindControl<Button>("OkButton").Content = okLabel;
        this.FindControl<Button>("CancelButton").Click += (_, _) => Close();
        this.FindControl<Button>("OkButton").Click += (_, _) => { Confirmed = true; Close(); };
    }
}
