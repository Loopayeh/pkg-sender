using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PkgSender.Views;

/// <summary>Resume a partial console file, overwrite it, or cancel.</summary>
public partial class CopyChoiceDialog : Window
{
    public enum Choice { Cancel, Resume, Overwrite }

    public Choice Result { get; private set; } = Choice.Cancel;

    public CopyChoiceDialog(string fileName, string remoteSize, string localSize)
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<TextBlock>("DetailText").Text =
            $"{fileName}\nOn console: {remoteSize} / local: {localSize}.";
        this.FindControl<Button>("CancelButton").Click += (_, _) => Close();
        this.FindControl<Button>("OverwriteButton").Click += (_, _) => { Result = Choice.Overwrite; Close(); };
        this.FindControl<Button>("ResumeButton").Click += (_, _) => { Result = Choice.Resume; Close(); };
    }
}
