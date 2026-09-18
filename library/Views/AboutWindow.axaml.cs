using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PkgSender.Views;

public partial class AboutWindow : Window
{
    private const string SupportAddr = "0x839a30D52Ef7D2b53e818b9931efd7FE6F472e50";
    private const string SupportUrl = "https://link.trustwallet.com/send?coin=20000714&address=0x839a30D52Ef7D2b53e818b9931efd7FE6F472e50";
    private const string LinksUrl = "https://loopayeh.github.io/";

    public AboutWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<TextBlock>("TitleText").Text =
            $"PKG Sender {LoopDPI.Core.UpdateService.AppVersion}";
        this.FindControl<Button>("AddrButton").Click += async (_, _) =>
        {
            try
            {
                await TopLevel.GetTopLevel(this)!.Clipboard!.SetTextAsync(SupportAddr);
                this.FindControl<Button>("AddrButton").Content = "copied ✓";
                await System.Threading.Tasks.Task.Delay(1200);
                this.FindControl<Button>("AddrButton").Content = SupportAddr;
            }
            catch { }
        };
        this.FindControl<Button>("LinksButton").Click += (_, _) => OpenUrl(LinksUrl);
        this.FindControl<Button>("SupportButton").Click += (_, _) => OpenUrl(SupportUrl);
        var settings = LoopDPI.Core.AppSettings.Load();
        var chk = this.FindControl<CheckBox>("UpdateCheckBox");
        chk.IsChecked = settings.UpdateCheck;
        chk.IsCheckedChanged += (_, _) =>
        {
            var st = LoopDPI.Core.AppSettings.Load();
            st.UpdateCheck = chk.IsChecked == true;
            st.Save();
        };
        this.FindControl<Button>("CloseButton").Click += (_, _) => Close();
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
