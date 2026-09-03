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

        // Scan-gun / keyboard entry feeds the UPC and cash-amount buffers.
        AddHandler(TextInputEvent, OnTextInput, handledEventsToo: false);
        AddHandler(KeyDownEvent, OnKeyDownHandler, handledEventsToo: false);

        // Headless capture mode for visual review: renders each screen to PNG and exits.
        if (Environment.GetEnvironmentVariable("POS_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Opened += (_, _) => _ = CaptureScreensAsync(dir);
        }

        // Terminal integration test: waits for the customer terminal to connect,
        // walks the AYS flow into a card tender, captures the result, and exits.
        if (Environment.GetEnvironmentVariable("POS_TERMINAL_TEST_DIR") is { Length: > 0 } testDir)
        {
            Opened += (_, _) => _ = TestTerminalAsync(testDir);
        }
    }

    private void PressT(MainViewModel vm, int index) =>
        vm.PressTKeyCommand.Execute(vm.TKeys[index - 1]);

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

        PressT(vm, 1);                  // loyalty lookup → scan
        vm.ScanUpc("3145891313406");    // Chanel Beaute
        PressT(vm, 1);                  // checkout
        await Shot("t1-checkout-connected");
        PressT(vm, 1);                  // Bloomingdale's Card/Pay → START_PAYMENT
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

        await Task.Delay(800);

        // Cash flow, matching the deck slide for slide.
        await Shot("01-loyalty");
        PressT(vm, 1);                  // lookup loyalty → B.TEST linked
        await Shot("02-scan-empty");
        PressT(vm, 5);                  // items page
        await Shot("02b-items");
        vm.AddCatalogItemCommand.Execute(vm.CatalogItems[0]); // Chanel Beaute 50.00
        vm.CloseItemsCommand.Execute(null);
        await Shot("03-scan-item");
        PressT(vm, 1);                  // checkout
        await Shot("04-checkout");
        PressT(vm, 8);                  // more payment methods
        await Shot("05-more-payments");
        PressT(vm, 3);                  // cash
        foreach (var c in "5083") vm.EntryChar(c);
        await Shot("06-cash");
        vm.EntrySubmit();
        await Shot("07-complete");

        // Card flow (loyalty bypassed, like the deck's YSL sale).
        vm.NewSaleCommand.Execute(null);
        vm.BypassCommand.Execute(null);
        vm.ScanUpc("3365440057838");    // Ysl Cosmetics 30.00
        PressT(vm, 1);                  // checkout
        PressT(vm, 1);                  // Bloomingdale's Card/Pay
        await Shot("08-card-tender");
        for (var i = 0; i < 30 && vm.Stage != RegisterStage.SignatureWait; i++) await Task.Delay(250);
        await Shot("09-signature");

        vm.NewSaleCommand.Execute(null);
        vm.OpenSetupCommand.Execute(null);
        await Shot("10-setup");
        Environment.Exit(0);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>
    /// The scan-gun handlers are window-wide, so they would otherwise swallow
    /// every keystroke typed into the setup screen's fields — and Enter would
    /// submit a bogus UPC instead of the IP being entered.
    /// </summary>
    private bool IsTypingInField() => FocusManager?.GetFocusedElement() is TextBox;

    /// <summary>
    /// True while a modal page owns the screen. Feeding the UPC buffer behind
    /// an open page lets a stray Enter submit a half-typed code, and gives the
    /// page's own buttons a second way to be triggered.
    /// </summary>
    private bool IsOverlayOpen() => Vm is { } vm && (vm.ShowItems || vm.ShowSetup);

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (Vm is not { } vm || string.IsNullOrEmpty(e.Text)) return;
        if (IsTypingInField() || IsOverlayOpen()) return;
        foreach (var c in e.Text)
        {
            vm.EntryChar(c);
        }
    }

    private void OnKeyDownHandler(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (IsTypingInField() || IsOverlayOpen()) return;
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
