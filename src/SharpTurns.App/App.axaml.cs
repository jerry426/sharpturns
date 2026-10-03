using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SharpTurns.App.Services;
using SharpTurns.App.ViewModels;
using SharpTurns.App.Views;
using SharpTurns.Core.InstanceManagement;
using SharpTurns.Core.Persistence;

namespace SharpTurns.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    // Several instances can share the database; each conversation's lock keeps two of them out of the same one.
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
                "SharpTurns");
            Directory.CreateDirectory(dataDirectory);
            var store = new ConversationStore(Path.Combine(dataDirectory, "sharpturns.db"));
            // Turns and summaries read the claude path from the preferences at launch, so a saved change applies next time.
            var preferences = new ApplicationPreferencesViewModel(store);
            var viewModel = new MainWindowViewModel(store, new ClaudeTurnRunner(store, () => preferences.ClaudePath),
                new TurnSummarizer(store, () => preferences.ClaudePath), preferences);
            _ = ShowMainWindowAsync(desktop, store, viewModel, AppInstanceLaunchEnvironment.ConsumeStartupSession());
        }

        base.OnFrameworkInitializationCompleted();
    }

    // The window opens once the preferences load, at the saved startup size, centered on the monitor it was last moved
    // to. InitializeAsync reports its own failures in the window. Then the instance reports to the Instance Manager and
    // lets it link the window.
    private static async Task ShowMainWindowAsync(IClassicDesktopStyleApplicationLifetime desktop, ConversationStore store,
        MainWindowViewModel viewModel, AppStartupSession? startupSession)
    {
        await viewModel.InitializeAsync(startupSession);
        var size = viewModel.Preferences.StartupWindow.Size;
        var window = new MainWindow { DataContext = viewModel, Width = size.Width, Height = size.Height };
        var savedScreen = await store.GetSettingAsync(WindowScreenSetting);
        CenterOnSavedScreen(window, savedScreen);
        // Saved when the window reaches another monitor rather than on close, since not every way out of the app closes
        // the window first. Saves run one after another, so the last monitor reached is the one kept.
        var saving = Task.CompletedTask;
        window.PositionChanged += (_, _) =>
        {
            if (ScreenSetting(window) is not { } screen || screen == savedScreen) return;
            savedScreen = screen;
            saving = SaveWindowScreenAsync(store, saving, screen);
        };
        var reporter = new InstanceReporterService(new InstanceManagerClient().ReportAsync, Environment.ProcessId,
            token => Dispatcher.UIThread.InvokeAsync(viewModel.CaptureInstanceSnapshot, DispatcherPriority.Background, token)
                .GetTask(),
            instanceId: Environment.GetEnvironmentVariable(AppInstanceLaunchEnvironment.InstanceIdVariable));
        viewModel.InstanceStateChanged += reporter.Emit;
        var windowControl = new WindowControlHost(window, Environment.ProcessId);
        window.Opened += (_, _) =>
        {
            windowControl.Start();
            _ = reporter.StartAsync();
        };
        desktop.Exit += (_, _) =>
        {
            windowControl.StopForApplicationExit();
            reporter.DisposeAsync().AsTask().GetAwaiter().GetResult();
        };
        desktop.MainWindow = window;
        window.Show();
    }

    private const string WindowScreenSetting = "startup_window_screen";

    // A monitor is matched by its name and its place in the arrangement, so two identical monitors stay apart; if the
    // arrangement changed, a name only one monitor has still matches. Otherwise the window stays on the main display.
    private static void CenterOnSavedScreen(Window window, string? json)
    {
        SavedScreen? saved;
        try { saved = json is null ? null : JsonSerializer.Deserialize<SavedScreen>(json); }
        catch (JsonException) { return; }
        if (saved is null) return;

        var screens = window.Screens.All;
        var bounds = new PixelRect(saved.X, saved.Y, saved.Width, saved.Height);
        var screen = screens.FirstOrDefault(s => s.DisplayName == saved.Name && s.Bounds == bounds)
            ?? (saved.Name is not null && screens.Where(s => s.DisplayName == saved.Name).ToList() is [var only] ? only : null);
        if (screen is null) return;

        var area = screen.WorkingArea;
        var width = (int)Math.Round(window.Width * screen.Scaling);
        var height = (int)Math.Round(window.Height * screen.Scaling);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(area.X + Math.Max(0, (area.Width - width) / 2),
            area.Y + Math.Max(0, (area.Height - height) / 2));
    }

    private static string? ScreenSetting(Window window)
    {
        if (window.Screens.ScreenFromWindow(window) is not { } screen) return null;
        var b = screen.Bounds;
        return JsonSerializer.Serialize(new SavedScreen(screen.DisplayName, b.X, b.Y, b.Width, b.Height));
    }

    private static async Task SaveWindowScreenAsync(ConversationStore store, Task previous, string screen)
    {
        await previous;
        try
        {
            await store.SetSettingAsync(WindowScreenSetting, screen);
        }
        catch (Exception)
        {
            // Remembering the monitor is a convenience; a failed save just leaves the previous one.
        }
    }

    private sealed record SavedScreen(string? Name, int X, int Y, int Width, int Height);
}
