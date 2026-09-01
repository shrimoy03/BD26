using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MerchantTerminal.ViewModels;

namespace MerchantTerminal.Views;

public partial class SetupOverlay : UserControl
{
    public SetupOverlay()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Pick the PAX client keystore off disk. Saves the operator typing a long
    /// path on a touchscreen when the .p12 lives on a USB stick.
    /// </summary>
    private async void OnBrowseCertificate(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select the PXRRS client certificate",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("PKCS#12 keystore") { Patterns = new[] { "*.p12", "*.pfx" } },
                new("All files") { Patterns = new[] { "*" } },
            },
        });

        if (files.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
        {
            vm.Setup.SetCertificatePath(path);
        }
    }
}
