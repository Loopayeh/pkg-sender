using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PkgSender;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
