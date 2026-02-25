using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Org.BouncyCastle.Crypto;
using PaymentGateway.Helpers;
using PaymentGateway.Models;
using PaymentGateway.Services;

namespace PaymentGateway.Controllers;

public class PaymentController : Controller
{
    private readonly ILogger<PaymentController> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPaymentDataStore _paymentDataStore;

    public PaymentController(
        ILogger<PaymentController> logger,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IPaymentDataStore paymentDataStore)
    {
        _logger = logger;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _paymentDataStore = paymentDataStore;
    }

    [HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery] string data, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Payment callback received. TraceId: {TraceId}, DataLength: {DataLength}, QueryKeys: {QueryKeys}",
            HttpContext.TraceIdentifier,
            data?.Length ?? 0,
            string.Join(",", Request.Query.Keys));

        if (string.IsNullOrWhiteSpace(data))
        {
            _logger.LogWarning("Payment callback rejected because data is missing. TraceId: {TraceId}", HttpContext.TraceIdentifier);
            return BadRequest("Missing data parameter.");
        }

        try
        {
            string decryptedJson = DecryptWithConfiguredKey(data);
            PaymentModel? paymentDetails = JsonSerializer.Deserialize<PaymentModel>(decryptedJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            });

            if (paymentDetails?.Invoice is null)
            {
                _logger.LogWarning("Invalid payment details payload. TraceId: {TraceId}", HttpContext.TraceIdentifier);
                return BadRequest("Invalid payment info.");
            }

            _logger.LogInformation(
                "Callback payload parsed. TraceId: {TraceId}, InvoiceReference: {InvoiceReference}, InvoiceUuid: {InvoiceUuid}, Amount: {Amount}, SpaceUuid: {SpaceUuid}",
                HttpContext.TraceIdentifier,
                paymentDetails.Invoice.Reference,
                paymentDetails.Invoice.Uuid,
                paymentDetails.Invoice.PriceWithDiscountWithTaxes,
                paymentDetails.Space?.Uuid);

            Guid indexGuid = await _paymentDataStore.SaveDecryptedPayloadAsync(paymentDetails, decryptedJson, HttpContext.TraceIdentifier, cancellationToken);
            _logger.LogInformation("Callback payload stored with IndexGuid: {IndexGuid}, TraceId: {TraceId}", indexGuid, HttpContext.TraceIdentifier);
            return RedirectToAction(nameof(Checkout), new { indexGuid });
        }
        catch (InvalidCipherTextException ex)
        {
            _logger.LogError(ex, "Failed to authenticate callback payload. TraceId: {TraceId}", HttpContext.TraceIdentifier);
            return BadRequest("Invalid encrypted payload.");
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "Callback payload is not valid base64. TraceId: {TraceId}", HttpContext.TraceIdentifier);
            return BadRequest("Invalid payload format.");
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Callback payload validation failed. TraceId: {TraceId}", HttpContext.TraceIdentifier);
            return BadRequest("Invalid payload format.");
        }
        catch (NpgsqlException ex)
        {
            _logger.LogError(ex, "Failed to insert decrypted callback data into database. TraceId: {TraceId}", HttpContext.TraceIdentifier);
            return StatusCode(StatusCodes.Status500InternalServerError, "Unable to store callback data.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process payment callback. TraceId: {TraceId}", HttpContext.TraceIdentifier);
            return BadRequest("Unable to process callback.");
        }
    }

    [HttpGet("payment/checkout/{indexGuid:guid}")]
    public async Task<IActionResult> Checkout(Guid indexGuid, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Checkout page requested. IndexGuid: {IndexGuid}", indexGuid);
        var session = await _paymentDataStore.GetByIndexGuidAsync(indexGuid, cancellationToken);
        if (session is null)
        {
            _logger.LogWarning("Checkout page request failed. Session not found. IndexGuid: {IndexGuid}", indexGuid);
            return NotFound("Payment session not found.");
        }

        return View(await BuildCheckoutViewModelAsync(session, cancellationToken));
    }

    [HttpPost("payment/checkout/{indexGuid:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Checkout(Guid indexGuid, long? channelId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Checkout submit received. IndexGuid: {IndexGuid}, ChannelId: {ChannelId}", indexGuid, channelId);
        var session = await _paymentDataStore.GetByIndexGuidAsync(indexGuid, cancellationToken);
        if (session is null)
        {
            _logger.LogWarning("Checkout submit failed. Session not found. IndexGuid: {IndexGuid}", indexGuid);
            return NotFound("Payment session not found.");
        }

        if (!channelId.HasValue || channelId.Value <= 0)
        {
            var invalidSelectionVm = await BuildCheckoutViewModelAsync(session, cancellationToken);
            invalidSelectionVm.ErrorMessage = "Please select a payment channel.";
            return View(invalidSelectionVm);
        }

        var allowedChannels = await _paymentDataStore.GetChannelOptionsAsync(session.Amount, cancellationToken);
        _logger.LogInformation(
            "Allowed channels evaluated. IndexGuid: {IndexGuid}, Amount: {Amount}, ChannelCount: {ChannelCount}",
            indexGuid, session.Amount, allowedChannels.Count);
        if (allowedChannels.Count == 0)
        {
            var noChannelsVm = await BuildCheckoutViewModelAsync(session, cancellationToken);
            noChannelsVm.ErrorMessage = "No payment channels are available for this payable amount.";
            return View(noChannelsVm);
        }

        var selectedChannel = allowedChannels.FirstOrDefault(c => c.Id == channelId.Value);
        if (selectedChannel is null)
        {
            var invalidChannelVm = await BuildCheckoutViewModelAsync(session, cancellationToken);
            invalidChannelVm.ErrorMessage = "Selected payment channel is not allowed for this payable amount.";
            invalidChannelVm.SelectedChannelId = channelId;
            return View(invalidChannelVm);
        }

        try
        {
            _logger.LogInformation(
                "Selected channel accepted. IndexGuid: {IndexGuid}, ChannelId: {ChannelId}, ChannelCode: {ChannelCode}, Country: {Country}, Currency: {Currency}",
                indexGuid, selectedChannel.Id, selectedChannel.Code, selectedChannel.Country, selectedChannel.Currency);
            string paymentUrl = await CreatePaymentRequestAndGetInvoiceUrlAsync(
                session,
                selectedChannel.Code,
                selectedChannel.Country,
                selectedChannel.Currency,
                indexGuid,
                cancellationToken);
            await _paymentDataStore.UpdatePaymentAttemptAsync(indexGuid, selectedChannel.Code, paymentUrl, cancellationToken);
            _logger.LogInformation("Checkout submit completed. Redirecting to payment URL. IndexGuid: {IndexGuid}, PaymentUrl: {PaymentUrl}", indexGuid, paymentUrl);
            return Redirect(paymentUrl);
        }
        catch (HttpRequestException ex)
        {
            await _paymentDataStore.UpdateStatusAsync(indexGuid, "Failed", cancellationToken);
            _logger.LogError(ex, "Xendit payment request API call failed. IndexGuid: {IndexGuid}", indexGuid);
            var failedVm = await BuildCheckoutViewModelAsync(session, cancellationToken);
            failedVm.ErrorMessage = "Unable to create payment request. Please try again.";
            return View(failedVm);
        }
        catch (NpgsqlException ex)
        {
            _logger.LogError(ex, "Database update failed for IndexGuid: {IndexGuid}", indexGuid);
            return StatusCode(StatusCodes.Status500InternalServerError, "Unable to update payment status.");
        }
    }

    [HttpGet("payment/success/{indexGuid:guid}")]
    public async Task<IActionResult> Success(Guid indexGuid, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Success endpoint hit. IndexGuid: {IndexGuid}", indexGuid);
        await _paymentDataStore.UpdateStatusAsync(indexGuid, "Success", cancellationToken);
        var session = await _paymentDataStore.GetByIndexGuidAsync(indexGuid, cancellationToken);
        if (session is null)
        {
            _logger.LogWarning("Success endpoint session not found after status update. IndexGuid: {IndexGuid}", indexGuid);
            return NotFound("Payment session not found.");
        }

        var vm = await BuildCheckoutViewModelAsync(session, cancellationToken);
        vm.Status = "Success";
        return View("Result", vm);
    }

    [HttpGet("payment/failed/{indexGuid:guid}")]
    public async Task<IActionResult> Failed(Guid indexGuid, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Failed endpoint hit. IndexGuid: {IndexGuid}", indexGuid);
        await _paymentDataStore.UpdateStatusAsync(indexGuid, "Failed", cancellationToken);
        var session = await _paymentDataStore.GetByIndexGuidAsync(indexGuid, cancellationToken);
        if (session is null)
        {
            _logger.LogWarning("Failed endpoint session not found after status update. IndexGuid: {IndexGuid}", indexGuid);
            return NotFound("Payment session not found.");
        }

        var vm = await BuildCheckoutViewModelAsync(session, cancellationToken);
        vm.Status = "Failed";
        vm.ErrorMessage = "Payment was not completed.";
        return View("Result", vm);
    }

    [HttpPost("payment/xendit/webhook")]
    public async Task<IActionResult> XenditWebhook([FromBody] JsonElement payload, CancellationToken cancellationToken)
    {
        var webhookRaw = payload.GetRawText();
        _logger.LogInformation("Webhook received. PayloadLength: {PayloadLength}", webhookRaw.Length);
        if (!TryExtractWebhookInfo(payload, out var indexGuid, out var status))
        {
            _logger.LogWarning("Webhook payload missing required fields. Payload: {Payload}", Truncate(webhookRaw, 2000));
            return BadRequest("Invalid webhook payload.");
        }

        await _paymentDataStore.UpdateStatusAsync(indexGuid, status, cancellationToken);
        _logger.LogInformation("Webhook updated payment status. IndexGuid: {IndexGuid}, Status: {Status}", indexGuid, status);
        return Ok(new { success = true });
    }

    private async Task<PaymentCheckoutViewModel> BuildCheckoutViewModelAsync(PaymentSessionRecord session, CancellationToken cancellationToken)
    {
        var channelOptions = await _paymentDataStore.GetChannelOptionsAsync(session.Amount, cancellationToken);
        _logger.LogDebug(
            "Checkout view model built. IndexGuid: {IndexGuid}, Amount: {Amount}, ChannelOptionCount: {ChannelOptionCount}",
            session.IndexGuid, session.Amount, channelOptions.Count);
        return new PaymentCheckoutViewModel
        {
            IndexGuid = session.IndexGuid,
            InvoiceReference = session.InvoiceReference,
            InvoiceUuid = session.InvoiceUuid,
            CustomerName = session.BilledEntityName,
            Amount = session.Amount,
            SpaceName = session.SpaceName,
            Status = session.Status,
            SelectedChannelId = null,
            SelectedChannelCode = session.SelectedChannelCode,
            ChannelOptions = channelOptions
        };
    }

    private async Task<string> CreatePaymentRequestAndGetInvoiceUrlAsync(
        PaymentSessionRecord session,
        string channelCode,
        string country,
        string currency,
        Guid indexGuid,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Preparing Xendit request. IndexGuid: {IndexGuid}, ChannelCode: {ChannelCode}, Country: {Country}, Currency: {Currency}, Amount: {Amount}",
            indexGuid, channelCode, country, currency, session.Amount);

        string? secretKey = _configuration["Xendit:SecretKey"];
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            throw new ArgumentException("Missing Xendit secret key configuration.", nameof(secretKey));
        }

        string apiVersion = _configuration["Xendit:ApiVersion"] ?? "2024-11-11";
        string statementDescriptor = _configuration["Xendit:StatementDescriptor"] ?? "Goods and Services";

        string successUrl = Url.Action(nameof(Success), "Payment", new { indexGuid }, Request.Scheme)
            ?? _configuration["Xendit:SuccessReturnUrl"]
            ?? throw new InvalidOperationException("Unable to build success return URL.");
        string failureUrl = Url.Action(nameof(Failed), "Payment", new { indexGuid }, Request.Scheme)
            ?? _configuration["Xendit:FailureReturnUrl"]
            ?? throw new InvalidOperationException("Unable to build failure return URL.");

        long amount = Convert.ToInt64(Math.Round(session.Amount, MidpointRounding.AwayFromZero));
        if (amount <= 0)
        {
            throw new ArgumentException("Invalid request amount from payload.");
        }
        var payload = new
        {
            external_id = session.InvoiceReference
                   ?? session.InvoiceUuid
                   ?? indexGuid.ToString("N"),

            amount = amount,

            description = session.BilledEntityName ?? "Payment request",

            customer = new
            {
                given_names = session.BilledEntityName
            },

            success_redirect_url = successUrl,
            failure_redirect_url = failureUrl,
            currency = currency,

            payment_methods = new[] { channelCode },

            items = new[]
     {
        new
        {
            name = session.SpaceName,
            price=amount,
            quantity=1
        }
    }
        };

        var client = _httpClientFactory.CreateClient("Xendit");
        using var request = new HttpRequestMessage(HttpMethod.Post, "v2/invoices");
        request.Headers.Add("api-version", apiVersion);
        string basicAuth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{secretKey}:"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);
        request.Content = JsonContent.Create(payload);
        string payloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        _logger.LogInformation("Request payload for v3/payment_requests. IndexGuid: {IndexGuid}, Payload: {Payload}", indexGuid, payloadJson);
        var sw = Stopwatch.StartNew();
        using var response = await client.SendAsync(request, cancellationToken);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        sw.Stop();
        _logger.LogInformation(
            "Xendit response received. IndexGuid: {IndexGuid}, StatusCode: {StatusCode}, DurationMs: {DurationMs}, Body: {Body}",
            indexGuid,
            (int)response.StatusCode,
            sw.ElapsedMilliseconds,
            Truncate(responseBody, 3000));

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Xendit API request failed with {(int)response.StatusCode}: {responseBody}");
        }

        string? invoiceUrl = ExtractInvoiceUrl(responseBody);
        if (string.IsNullOrWhiteSpace(invoiceUrl))
        {
            throw new InvalidOperationException("invoice_url not found in Xendit response.");
        }

        return invoiceUrl;
    }

    private static string? ExtractInvoiceUrl(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (TryGetString(root, "invoice_url", out var invoiceUrl))
        {
            return invoiceUrl;
        }

        if (TryGetString(root, "payment_url", out var paymentUrl))
        {
            return paymentUrl;
        }

        if (root.TryGetProperty("actions", out var actions))
        {
            if (TryGetString(actions, "desktop_web_checkout_url", out var desktopUrl))
            {
                return desktopUrl;
            }

            if (TryGetString(actions, "mobile_web_checkout_url", out var mobileUrl))
            {
                return mobileUrl;
            }
        }

        return null;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryExtractWebhookInfo(JsonElement payload, out Guid indexGuid, out string status)
    {
        indexGuid = Guid.Empty;
        status = "Failed";

        JsonElement root = payload;
        if (root.TryGetProperty("data", out var dataNode) && dataNode.ValueKind == JsonValueKind.Object)
        {
            root = dataNode;
        }

        if (root.TryGetProperty("metadata", out var metadata) &&
            metadata.ValueKind == JsonValueKind.Object &&
            TryGetString(metadata, "index_guid", out var guidText) &&
            Guid.TryParse(guidText, out var parsedGuid))
        {
            indexGuid = parsedGuid;
        }

        if (indexGuid == Guid.Empty &&
            TryGetString(root, "reference_id", out var referenceText) &&
            Guid.TryParse(referenceText, out parsedGuid))
        {
            indexGuid = parsedGuid;
        }

        if (indexGuid == Guid.Empty)
        {
            return false;
        }

        string? statusText = null;
        if (TryGetString(root, "status", out var v1))
        {
            statusText = v1;
        }
        else if (TryGetString(root, "payment_status", out var v2))
        {
            statusText = v2;
        }

        string normalized = (statusText ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized is "SUCCEEDED" or "SUCCESS" or "PAID" or "COMPLETED" or "SETTLED")
        {
            status = "Success";
            return true;
        }

        if (normalized is "FAILED" or "EXPIRED" or "CANCELED" or "CANCELLED")
        {
            status = "Failed";
            return true;
        }

        return false;
    }

    private string DecryptWithConfiguredKey(string data)
    {
        string? configuredAppKey = _configuration["Xendit:AppKey"];
        var candidates = new List<(string Name, byte[] Key)>();

        if (!string.IsNullOrWhiteSpace(configuredAppKey))
        {
            string key32Chars = configuredAppKey.Length > 32
                ? configuredAppKey[..32]
                : configuredAppKey;
            candidates.Add(("appkey-utf8-first32", Encoding.UTF8.GetBytes(key32Chars)));

            try
            {
                byte[] decoded = Convert.FromBase64String(configuredAppKey);
                candidates.Add(("appkey-base64", decoded));
            }
            catch (FormatException)
            {
            }
        }

        candidates.Add(("fallback-base64-constant", Convert.FromBase64String("GNA3HyFn2G4quTorrGAOhR/Y93QVp6juyyhPeqPoa8U=")));

        Exception? lastFailure = null;
        _logger.LogDebug("Trying callback decryption with {CandidateCount} key candidates.", candidates.Count);
        foreach (var candidate in candidates)
        {
            try
            {
                string decrypted = CryptoHelper.DecryptAesGcmFromBase64(candidate.Key, data);
                _logger.LogInformation("Callback decrypted using key mode: {KeyMode}", candidate.Name);
                return decrypted;
            }
            catch (InvalidCipherTextException ex)
            {
                _logger.LogWarning("Decryption failed for key mode {KeyMode}: {Error}", candidate.Name, ex.Message);
                lastFailure = ex;
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning("Decryption argument failure for key mode {KeyMode}: {Error}", candidate.Name, ex.Message);
                lastFailure = ex;
            }
        }

        throw lastFailure ?? new InvalidCipherTextException("Unable to decrypt payload with configured key candidates.");
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...(truncated)";
    }
}
