using Avalonia;

namespace SharpTurns.InstanceManager.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        if (OperatingSystem.IsMacOS())
        {
            // As in SharpTurns: avoid Metal's stale surface size/scale race during window resizing, which can stretch
            // the UI. Linked windows resize this one whenever a SharpTurns window is resized.
            builder.With(new AvaloniaNativePlatformOptions
            {
                RenderingMode =
                [
                    AvaloniaNativeRenderingMode.OpenGl,
                    AvaloniaNativeRenderingMode.Software,
                ],
            });
        }

        return builder;
    }
}
