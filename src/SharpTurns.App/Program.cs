using Avalonia;

namespace SharpTurns.App;

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
            // Avoid Metal's stale surface size/scale race during window resizing, which can stretch the UI.
            // Revisit once https://github.com/AvaloniaUI/Avalonia/pull/22215 ships in our Avalonia version.
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
