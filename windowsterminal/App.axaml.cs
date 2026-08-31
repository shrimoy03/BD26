using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MerchantTerminal.Services;
using MerchantTerminal.ViewModels;
using MerchantTerminal.Views;

namespace MerchantTerminal;

public partial class App : Application
{
    private ITerminalLink? _terminalLink;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // POS_TERMINAL_LINK selects the customer-terminal transport:
            //   jpxss -> PAX-agreed REST flow (JPxSerialServer over USB, or
            //            PXRRS on the terminal directly over Ethernet/Wi-Fi)
            //   pcl   -> JPxSerialServer's raw PCL TCP socket
            //   else  -> the Wi-Fi WebSocket server (default)
            _terminalLink = System.Environment.GetEnvironmentVariable("POS_TERMINAL_LINK") switch
            {
                "jpxss" => new CompositeLink(new JpxRestLink(), new TerminalLink()),
                "pcl" => new PclSocketLink(),
                _ => (ITerminalLink)new TerminalLink(),
            };
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
