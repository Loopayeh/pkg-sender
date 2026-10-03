using Foundation;
using UIKit;

namespace PkgSender.iOS;

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }
    MainViewController? _main;

    public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
    {
        // Technical tool with IP addresses + numeric fields: force LTR so RTL
        // system locales (e.g. Persian) don't mirror/shift the scroll content.
        // (Direct views only — SemanticContentAttribute doesn't compile on the
        // UIView appearance proxy.)
        Window = new UIWindow(UIScreen.MainScreen.Bounds);
        Window.SemanticContentAttribute = UISemanticContentAttribute.ForceLeftToRight;
        _main = new MainViewController();
        var nav = new UINavigationController(_main);
        nav.View.SemanticContentAttribute = UISemanticContentAttribute.ForceLeftToRight;
        Window.RootViewController = nav;
        Window.MakeKeyAndVisible();
        return true;
    }

    // Files app → Share/Open in "PKG Sender": import the file.
    public override bool OpenUrl(UIApplication app, NSUrl url, NSDictionary options)
    {
        if (_main != null && url != null)
        {
            _ = _main.ImportExternalAsync(url);
            return true;
        }
        return false;
    }
}

public static class Program
{
    static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}
