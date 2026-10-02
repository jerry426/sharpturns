using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SharpTurns.MarkdownViewer.App.Services;
using SharpTurns.MarkdownViewer.App.Startup;

namespace SharpTurns.MarkdownViewer.App;

public sealed partial class App : Application
{
    private ViewerWindowCoordinator? _windowCoordinator;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _windowCoordinator = new ViewerWindowCoordinator(desktop);
            if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
            {
                activatable.Activated += OnActivated;
            }

            _windowCoordinator.OpenInitialWindow(StartupOptions.Parse(Program.StartupArguments));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void OnActivated(object? sender, ActivatedEventArgs e)
    {
        if (e is FileActivatedEventArgs fileArguments && _windowCoordinator is not null)
        {
            await _windowCoordinator.OpenActivatedFilesAsync(fileArguments.Files);
        }
    }
}
