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
            // The transport is chosen by the saved link mode (setup screen, F9)
            // with POS_TERMINAL_LINK still overriding it:
            //   jpxss -> PAX-agreed REST flow (JPxSerialServer over USB, or
            //            PXRRS on the terminal directly over Ethernet/Wi-Fi)
            //   pcl   -> JPxSerialServer's raw PCL TCP socket
            //   else  -> the Wi-Fi WebSocket server (default)
            // The host owns the live link so the setup screen can retarget a
            // different terminal IP without restarting the register.
            var host = new TerminalLinkHost();
            _terminalLink = host;
            _ = host.ApplyAsync(PosSettings.Load()).ContinueWith(
                t => System.Console.WriteLine($"[TerminalLink] failed to start: {t.Exception?.GetBaseException().Message}"),
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(host),
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
