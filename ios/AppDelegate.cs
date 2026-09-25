using Foundation;
using UIKit;

namespace PkgSender.iOS;

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
    {
        // Technical tool with IP addresses + numeric fields: force LTR so RTL
        // system locales (e.g. Persian) don't mirror/shift the scroll content.
        UIView.Appearance.SemanticContentAttribute = UISemanticContentAttribute.ForceLeftToRight;
        Window = new UIWindow(UIScreen.MainScreen.Bounds);
        Window.RootViewController = new UINavigationController(new MainViewController());
        Window.MakeKeyAndVisible();
        return true;
    }
}

public static class Program
{
    static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}
