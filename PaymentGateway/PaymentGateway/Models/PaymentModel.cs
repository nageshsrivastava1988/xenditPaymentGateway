namespace PaymentGateway.Models;
public class PaymentModel
{
    [JsonPropertyName("invoice")]
    public Invoice? Invoice { get; set; }

    [JsonPropertyName("space")]
    public Space? Space { get; set; }
}

public class Invoice
{
    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }

    [JsonPropertyName("reference")]
    public string? Reference { get; set; }

    [JsonPropertyName("billed_entity_name")]
    public string? BilledEntityName { get; set; }

    [JsonPropertyName("price_with_discount_with_taxes")]
    public decimal PriceWithDiscountWithTaxes { get; set; }
}

public class Space
{
    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}