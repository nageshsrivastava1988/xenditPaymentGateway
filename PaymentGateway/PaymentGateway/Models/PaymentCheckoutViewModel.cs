namespace PaymentGateway.Models;

public sealed class PaymentCheckoutViewModel
{
    public Guid IndexGuid { get; set; }
    public string? InvoiceReference { get; set; }
    public string? InvoiceUuid { get; set; }
    public string? CustomerName { get; set; }
    public decimal Amount { get; set; }
    public string Country { get; set; } = "PH";
    public string Currency { get; set; } = "PHP";
    public string? SpaceName { get; set; }
    public string Status { get; set; } = "Pending";
    public string? ErrorMessage { get; set; }
    public long? SelectedChannelId { get; set; }
    public string? SelectedChannelCode { get; set; }
    public List<PaymentChannelOptionViewModel> ChannelOptions { get; set; } = [];
}

public sealed class PaymentChannelOptionViewModel
{
    public long Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
}

public sealed class PaymentSessionRecord
{
    public Guid IndexGuid { get; set; }
    public string? TraceId { get; set; }
    public string? InvoiceUuid { get; set; }
    public string? InvoiceReference { get; set; }
    public string? BilledEntityName { get; set; }
    public decimal Amount { get; set; }
    public string? SpaceUuid { get; set; }
    public string? SpaceName { get; set; }
    public string DecryptedJson { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string? SelectedChannelCode { get; set; }
    public string? PaymentUrl { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
