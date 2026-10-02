using Avalonia;

namespace SharpTurns.MarkdownViewer.App;

internal static class Program
{
    internal static IReadOnlyList<string> StartupArguments { get; private set; } = [];

    [STAThread]
    public static void Main(string[] args)
    {
        StartupArguments = args;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
