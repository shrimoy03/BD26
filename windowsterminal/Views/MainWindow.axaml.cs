using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using MerchantTerminal.ViewModels;

namespace MerchantTerminal.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Kiosk mode on the Windows all-in-one; regular window while developing on macOS.
        if (OperatingSystem.IsWindows())
        {
            WindowState = WindowState.FullScreen;
        }

        // Scan-gun / keyboard SKU entry feeds the entry bar.
        AddHandler(TextInputEvent, OnTextInput, handledEventsToo: false);
        AddHandler(KeyDownEvent, OnKeyDownHandler, handledEventsToo: false);

        // Headless capture mode for visual review: renders each screen to PNG and exits.
        if (Environment.GetEnvironmentVariable("POS_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Opened += (_, _) => _ = CaptureScreensAsync(dir);
        }

        // Terminal integration test: waits for the customer terminal to connect,
        // starts a card tender, captures the result, and exits.
        if (Environment.GetEnvironmentVariable("POS_TERMINAL_TEST_DIR") is { Length: > 0 } testDir)
        {
            Opened += (_, _) => _ = TestTerminalAsync(testDir);
        }
    }

    private async Task TestTerminalAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        if (Vm is not { } vm) return;

        async Task Shot(string name)
        {
            await Task.Delay(400);
            var rtb = new RenderTargetBitmap(new PixelSize(1600, 1000), new Vector(96, 96));
            rtb.Render(RootCanvas);
            rtb.Save(Path.Combine(dir, name + ".png"));
        }

        // Give the simulated Android client time to connect.
        for (var i = 0; i < 40 && !vm.IsTerminalConnected; i++) await Task.Delay(250);

        vm.OpenTenderCommand.Execute(null);
        await Shot("t1-tender-connected");
        vm.TenderCardCommand.Execute(null);
        await Shot("t2-awaiting-terminal");
        for (var i = 0; i < 40 && vm.IsAwaitingTerminal; i++) await Task.Delay(250);
        await Shot("t3-terminal-result");
        Environment.Exit(0);
    }

    private async Task CaptureScreensAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        if (Vm is not { } vm) return;

        async Task Shot(string name)
        {
            await Task.Delay(400);
            var rtb = new RenderTargetBitmap(new PixelSize(1600, 1000), new Vector(96, 96));
            rtb.Render(RootCanvas);
            rtb.Save(Path.Combine(dir, name + ".png"));
        }

        await Task.Delay(1000);
        await Shot("01-register-dark");
        vm.OpenCustomerCommand.Execute(null);
        await Shot("02-customer-dark");
        vm.PickCustomerCommand.Execute(vm.Customers[0]);
        vm.OpenPromosCommand.Execute(null);
        await Shot("03-promos-dark");
        vm.TogglePromoCommand.Execute(vm.Promos[0]);
        vm.CloseOverlayCommand.Execute(null);
        vm.OpenTenderCommand.Execute(null);
        await Shot("04-tender-dark");
        vm.TenderCashCommand.Execute(null);
        await Shot("05-tender-paid-dark");
        vm.FinishCommand.Execute(null);
        await Shot("06-done-dark");
        vm.NewSaleCommand.Execute(null);
        vm.ToggleThemeCommand.Execute(null);
        await Shot("07-register-light-empty");
        vm.AddItemCommand.Execute(vm.QuickKeys[1]);
        vm.AddItemCommand.Execute(vm.QuickKeys[2]);
        await Shot("08-register-light");
        vm.OpenSetupCommand.Execute(null);
        await Shot("09-setup-light");
        vm.ToggleThemeCommand.Execute(null);
        await Shot("10-setup-dark");
        Environment.Exit(0);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>
    /// The scan-gun handlers are window-wide, so they would otherwise swallow
    /// every keystroke typed into the setup screen's fields — and Enter would
    /// submit a bogus SKU instead of the IP being entered.
    /// </summary>
    private bool IsTypingInField() => FocusManager?.GetFocusedElement() is TextBox;

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (Vm is not { } vm || string.IsNullOrEmpty(e.Text)) return;
        if (IsTypingInField()) return;
        foreach (var c in e.Text)
        {
            vm.EntryChar(c);
        }
    }

    private void OnKeyDownHandler(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (IsTypingInField()) return;
        switch (e.Key)
        {
            case Key.Enter:
                vm.EntrySubmit();
                e.Handled = true;
                break;
            case Key.Back:
                vm.EntryBackspace();
                e.Handled = true;
                break;
        }
    }
}
