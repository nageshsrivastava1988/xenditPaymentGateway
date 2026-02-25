using System;

namespace PaymentGateway.Models;

public class PaymentLinkResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("external_id")]
    public string? ExternalId { get; set; }

    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("merchant_name")]
    public string? MerchantName { get; set; }

    [JsonPropertyName("merchant_profile_picture_url")]
    public string? MerchantProfilePictureUrl { get; set; }

    [JsonPropertyName("amount")]
    public long Amount { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("expiry_date")]
    public DateTime ExpiryDate { get; set; }

    [JsonPropertyName("invoice_url")]
    public string? InvoiceUrl { get; set; }

    [JsonPropertyName("available_banks")]
    public List<AvailableBank>? AvailableBanks { get; set; }

    [JsonPropertyName("available_retail_outlets")]
    public List<AvailableRetailOutlet>? AvailableRetailOutlets { get; set; }

    [JsonPropertyName("available_ewallets")]
    public List<AvailableEwallet>? AvailableEwallets { get; set; }

    [JsonPropertyName("available_qr_codes")]
    public List<AvailableQrCode>? AvailableQrCodes { get; set; }

    [JsonPropertyName("available_direct_debits")]
    public List<AvailableDirectDebit>? AvailableDirectDebits { get; set; }

    [JsonPropertyName("available_paylaters")]
    public List<object>? AvailablePaylaters { get; set; }

    [JsonPropertyName("should_exclude_credit_card")]
    public bool ShouldExcludeCreditCard { get; set; }

    [JsonPropertyName("should_send_email")]
    public bool ShouldSendEmail { get; set; }

    [JsonPropertyName("success_redirect_url")]
    public string? SuccessRedirectUrl { get; set; }

    [JsonPropertyName("failure_redirect_url")]
    public string? FailureRedirectUrl { get; set; }

    [JsonPropertyName("created")]
    public DateTime Created { get; set; }

    [JsonPropertyName("updated")]
    public DateTime Updated { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("items")]
    public List<Item>? Items { get; set; }

    [JsonPropertyName("customer")]
    public Customer? Customer { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }
}
public class AvailableBank
{
    [JsonPropertyName("bank_code")]
    public string? BankCode { get; set; }

    [JsonPropertyName("collection_type")]
    public string? CollectionType { get; set; }

    [JsonPropertyName("transfer_amount")]
    public long TransferAmount { get; set; }

    [JsonPropertyName("bank_branch")]
    public string? BankBranch { get; set; }

    [JsonPropertyName("account_holder_name")]
    public string? AccountHolderName { get; set; }

    [JsonPropertyName("identity_amount")]
    public long IdentityAmount { get; set; }
}

public class AvailableRetailOutlet
{
    [JsonPropertyName("retail_outlet_name")]
    public string? RetailOutletName { get; set; }
}

public class AvailableEwallet
{
    [JsonPropertyName("ewallet_type")]
    public string? EwalletType { get; set; }
}

public class AvailableQrCode
{
    [JsonPropertyName("qr_code_type")]
    public string? QrCodeType { get; set; }
}

public class AvailableDirectDebit
{
    [JsonPropertyName("direct_debit_type")]
    public string? DirectDebitType { get; set; }
}
