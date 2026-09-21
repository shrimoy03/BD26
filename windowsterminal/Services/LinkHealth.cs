using System;

namespace MerchantTerminal.Services;

/// <summary>
/// One-line verdict on why the terminal link is failing, for the status bar.
/// Null means healthy. Raised by the transport, consumed by the view model;
/// static so it needs no plumbing through the link interfaces.
/// </summary>
public static class LinkHealth
{
    public static event Action<string?>? Changed;

    public static string? Current { get; private set; }

    public static void Report(string? verdict)
    {
        if (verdict == Current) return;
        Current = verdict;
        Changed?.Invoke(verdict);
    }
}
