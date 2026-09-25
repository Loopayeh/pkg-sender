using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PkgSender.Views;

public partial class GuideWindow : Window
{
    public GuideWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Button>("CloseButton").Click += (_, _) => Close();
        if (!OperatingSystem.IsWindows())
        {
            this.FindControl<TextBlock>("DirectPcStep").Text =
                "1) PC — network settings of your desktop (NetworkManager: IPv4 → Manual): " +
                "192.168.10.1 / mask 255.255.255.0, gateway and DNS empty.";
            this.FindControl<TextBlock>("FirewallTip").Text =
                "• Push goes through but the download never starts — allow inbound TCP port 9898 " +
                "(e.g. sudo ufw allow 9898/tcp, or firewalld: sudo firewall-cmd --permanent --add-port=9898/tcp).";
        }
    }
}
