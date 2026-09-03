using System.Text.Json;
using System.Text.Json.Serialization;

namespace MerchantTerminal.Models;

/// <summary>
/// Wire protocol shared with the Android customer-facing app (winkpos).
/// Every WebSocket text frame is one JSON-encoded PosMessage.
/// </summary>
public sealed record PosMessage
{
    public string Type { get; init; } = "";

    // START_PAYMENT / CANCEL_PAYMENT / PAYMENT_RESULT
    public string? OrderId { get; init; }
    public long? AmountCents { get; init; }
    public string? Currency { get; init; }

    // PAYMENT_RESULT
    public string? Status { get; init; }   // APPROVED | DECLINED | CANCELLED
    public string? Method { get; init; }   // e.g. WINK | CARD
    public string? Reason { get; init; }
    public string? Token { get; init; }    // card token for the gateway simulator

    // DISPLAY_CART — basket mirrored onto the customer terminal
    public CartLine[]? Items { get; init; }
    public long? SubtotalCents { get; init; }
    public long? TaxCents { get; init; }
}

/// <summary>One basket line inside a DISPLAY_CART message.</summary>
public sealed record CartLine(string Name, int Qty, long AmountCents);

public static class PosMessageTypes
{
    // Terminal -> Android
    public const string StartPayment = "START_PAYMENT";
    public const string CancelPayment = "CANCEL_PAYMENT";
    public const string DisplayCart = "DISPLAY_CART";
    public const string ShowThanks = "SHOW_THANKS";
    public const string ShowRetailer = "SHOW_RETAILER";

    // PxRetailer form -> Terminal (PAYMENTSTATUS FireEvent, via PXRRS notify)
    public const string TenderSelected = "TENDER_SELECTED";

    // Android -> Terminal
    public const string Hello = "HELLO";
    public const string PaymentResult = "PAYMENT_RESULT";
}

public static class PosJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(PosMessage message) =>
        JsonSerializer.Serialize(message, Options);

    public static PosMessage? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PosMessage>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
