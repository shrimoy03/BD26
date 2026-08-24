using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MerchantTerminal.Services;
using MerchantTerminal.ViewModels;
using MerchantTerminal.Views;

namespace MerchantTerminal;

public partial class App : Application
{
    private TerminalLink? _terminalLink;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _terminalLink = new TerminalLink();
            _ = _terminalLink.StartAsync().ContinueWith(
                t => System.Console.WriteLine($"[TerminalLink] failed to start: {t.Exception?.GetBaseException().Message}"),
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(_terminalLink),
            };

            desktop.ShutdownRequested += async (_, _) =>
            {
                if (_terminalLink is not null)
                {
                    await _terminalLink.DisposeAsync();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
