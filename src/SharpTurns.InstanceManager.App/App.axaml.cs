using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using SharpTurns.Core.InstanceManagement;
using SharpTurns.Core.Persistence;
using SharpTurns.InstanceManager.App.Services;
using SharpTurns.InstanceManager.App.ViewModels;
using SharpTurns.InstanceManager.App.Views;

namespace SharpTurns.InstanceManager.App;

public sealed partial class App : Application
{
    // SharpTurns' own default startup height; linked windows share the manager's height.
    private const double DefaultHeight = 860;
    private const double MinimumHeight = 560;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            InstanceManagerServer? server;
            try
            {
                server = InstanceManagerServer.TryCreate();
            }
            catch (Exception ex)
            {
                ShowMessageWindow(desktop, "SharpTurns Instance Manager",
                    $"Could not start local instance communication: {ex.Message}");
                base.OnFrameworkInitializationCompleted();
                return;
            }

            if (server is null)
            {
                // Another manager is already running. Ask it to focus its window, then exit.
                try
                {
                    new InstanceManagerClient().FocusAsync().GetAwaiter().GetResult();
                }
                catch
                {
                    // Best-effort; exit regardless.
                }
                Environment.Exit(0);
                return;
            }

            var launcher = new InstanceLauncher();
            var focuser = new InstanceFocuser();
            var mainWindow = new MainWindow { Height = LoadStartupWindowHeight() };
            var windowCoordinator = new LinkedWindowCoordinator(mainWindow);
            var viewModel = new InstanceManagerViewModel(
                server,
                launcher,
                focuser,
                windowCoordinator,
                confirmEndProcess: card => ConfirmEndProcessAsync(mainWindow, card),
                confirmReload: card => ConfirmEndProcessAsync(mainWindow, card, reload: true));
            mainWindow.DataContext = viewModel;
            desktop.MainWindow = mainWindow;

            mainWindow.Opened += StartViewModel;
            void StartViewModel(object? sender, EventArgs args)
            {
                mainWindow.Opened -= StartViewModel;
                _ = viewModel.StartAsync();
            }

            desktop.Exit += (_, _) =>
            {
                viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
                server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            };

            server.FocusRequested += () => Dispatcher.UIThread.Post(() => FocusMainWindow(mainWindow));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void FocusMainWindow(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }
        window.Activate();
    }

    // The manager opens at SharpTurns' saved startup height, since linking gives the windows the
    // manager's height. The database is only read, and only if SharpTurns has created it.
    private static double LoadStartupWindowHeight()
    {
        try
        {
            var database = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SharpTurns", "sharpturns.db");
            if (!File.Exists(database))
                return DefaultHeight;
            var setting = new ConversationStore(database).GetSettingAsync("startup_window_size").GetAwaiter().GetResult();
            return setting?.Split(',') is [_, var height]
                   && double.TryParse(height, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                   && parsed >= MinimumHeight
                ? parsed
                : DefaultHeight;
        }
        catch
        {
            // The manager can still start at the default height if the optional preference read fails.
            return DefaultHeight;
        }
    }

    private static void ShowMessageWindow(
        IClassicDesktopStyleApplicationLifetime desktop,
        string title,
        string message)
    {
        desktop.MainWindow = new Window
        {
            Title = title,
            Width = 560,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20),
                FontSize = 14,
            },
        };
    }

    private static async Task<bool> ConfirmEndProcessAsync(Window owner, InstanceCardViewModel card, bool reload = false)
    {
        var project = card.ProjectTitle ?? "(no project)";
        var conversation = card.ConversationTitle ?? "(no conversation)";
        var message =
            $"PID {card.Pid}\n{project} / {conversation}\n\n" +
            (reload
                ? "Reload will terminate this instance and its child processes, then reopen the same project and conversation. Unsent drafts and attached images will be lost."
                : "Ending the process will terminate this instance and any processes it spawned. It is currently idle (no turn or summary in progress).");

        var dialog = new ConfirmEndProcessDialog(reload) { Message = message };
        return await dialog.ShowDialog<bool>(owner);
    }
}
