using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PkgSender.Views;

public partial class GuideWindow : Window
{
    public GuideWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Button>("CloseButton").Click += (_, _) => Close();
    }
}
