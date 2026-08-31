using System;
using System.Threading.Tasks;
using MerchantTerminal.Models;

namespace MerchantTerminal.Services;

/// <summary>
/// Pairs the PXRRS REST link (PxRetailer forms/EMV on the PAX) with the
/// WebSocket link (winkpos, the Bloomingdale's WinkPay app on the same PAX,
/// connected via Wi-Fi or adb-reverse over USB). One terminal, two apps:
///
///   DISPLAY_CART                  -> both (PxRetailer renders; winkpos may mirror)
///   START_PAYMENT Method=FACE/PALM -> ws only  (WinkPay biometric flow)
///   START_PAYMENT otherwise        -> REST only (PxRetailer forms / EMV)
///   CANCEL_PAYMENT                 -> both
///   SHOW_THANKS                    -> REST only
///
/// Inbound events (PAYMENTSTATUS from the form, PAYMENT_RESULT from winkpos)
/// bubble up from either side. Connected == the REST side is up (the ws side
/// joins opportunistically).
/// </summary>
public sealed class CompositeLink : ITerminalLink
{
    public event Action? ClientConnected;
    public event Action? ClientDisconnected;
    public event Action<PosMessage>? MessageReceived;

    private readonly ITerminalLink _rest;
    private readonly ITerminalLink _ws;
    private bool _lastConnected;

    public CompositeLink(ITerminalLink rest, ITerminalLink ws)
    {
        _rest = rest;
        _ws = ws;
        foreach (var link in new[] { _rest, _ws })
        {
            link.ClientConnected += RaiseState;
            link.ClientDisconnected += RaiseState;
            link.MessageReceived += m => MessageReceived?.Invoke(m);
        }
    }

    public bool IsConnected => _rest.IsConnected || _ws.IsConnected;

    private void RaiseState()
    {
        var now = IsConnected;
        if (now == _lastConnected) return;
        _lastConnected = now;
        if (now) ClientConnected?.Invoke();
        else ClientDisconnected?.Invoke();
    }

    public async Task StartAsync()
    {
        await _rest.StartAsync();
        await _ws.StartAsync();
    }

    public async Task<bool> SendAsync(PosMessage message)
    {
        switch (message.Type)
        {
            case PosMessageTypes.StartPayment
                when message.Method is "FACE" or "PALM":
                return await _ws.SendAsync(message);
            case PosMessageTypes.StartPayment:
                return await _rest.SendAsync(message);
            case PosMessageTypes.ShowThanks:
                return await _rest.SendAsync(message);
            case PosMessageTypes.CancelPayment:
            case PosMessageTypes.DisplayCart:
            default:
                var restOk = await _rest.SendAsync(message);
                var wsOk = await _ws.SendAsync(message);
                return restOk || wsOk;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _rest.DisposeAsync();
        await _ws.DisposeAsync();
    }
}
