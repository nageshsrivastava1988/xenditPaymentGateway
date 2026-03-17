using System;

namespace PaymentGateway.Models;

public class PaymentAccount
{
 [JsonPropertyName("uuid")]
    public string Uuid { get; set; }

    [JsonPropertyName("creation_date")]
    public DateTime CreationDate { get; set; }

    [JsonPropertyName("update_date")]
    public DateTime UpdateDate { get; set; }

    [JsonPropertyName("href")]
    public string Href { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; }
}
