using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SharpTurns.App.Services;
using SharpTurns.App.ViewModels;
using SharpTurns.App.Views;
using SharpTurns.Core.Persistence;

namespace SharpTurns.App;

public sealed partial class App : Application
{
    // Held open for the app's lifetime; a second instance cannot lock it.
    private FileStream? _instanceLock;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
                "SharpTurns");
            Directory.CreateDirectory(dataDirectory);
            try
            {
                _instanceLock = new FileStream(Path.Combine(dataDirectory, "sharpturns.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                desktop.MainWindow = new Window
                {
                    Title = "SharpTurns",
                    Width = 420,
                    Height = 120,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Content = new TextBlock
                    {
                        Text = "SharpTurns is already running.",
                        Margin = new Thickness(24),
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    },
                };
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var store = new ConversationStore(Path.Combine(dataDirectory, "sharpturns.db"));
            var viewModel = new MainWindowViewModel(store, new ClaudeTurnRunner(store));
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.Exit += (_, _) => _instanceLock?.Dispose();
            // InitializeAsync reports its own failures in the window.
            _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
