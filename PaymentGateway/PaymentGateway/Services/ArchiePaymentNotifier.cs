namespace PaymentGateway.Services;

public interface IArchiePaymentNotifier
{
    Task NotifyPaymentResultIfNeededAsync(Guid indexGuid, string paymentStatus, CancellationToken cancellationToken);
}

public sealed class ArchiePaymentNotifier : IArchiePaymentNotifier
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> NotificationLocks = new();

    private readonly IConfiguration _configuration;
    private readonly ILogger<ArchiePaymentNotifier> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPaymentDataStore _paymentDataStore;

    public ArchiePaymentNotifier(
        IConfiguration configuration,
        ILogger<ArchiePaymentNotifier> logger,
        IHttpClientFactory httpClientFactory,
        IPaymentDataStore paymentDataStore)
    {
        _configuration = configuration;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _paymentDataStore = paymentDataStore;
    }

    public async Task NotifyPaymentResultIfNeededAsync(Guid indexGuid, string paymentStatus, CancellationToken cancellationToken)
    {
        string normalizedStatus = NormalizePaymentStatus(paymentStatus);
        string lockKey = $"{indexGuid:N}:{normalizedStatus}";
        SemaphoreSlim gate = NotificationLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken);
        try
        {
            PaymentSessionRecord? session = await _paymentDataStore.GetByIndexGuidAsync(indexGuid, cancellationToken);
            if (session is null)
            {
                _logger.LogWarning("Archie notification skipped because payment session was not found. IndexGuid: {IndexGuid}", indexGuid);
                return;
            }

            if (string.Equals(session.ArchieNotifiedStatus, normalizedStatus, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "Archie notification skipped because status was already sent. IndexGuid: {IndexGuid}, Status: {Status}, NotifiedAtUtc: {NotifiedAtUtc}",
                    indexGuid,
                    normalizedStatus,
                    session.ArchieNotifiedAtUtc);
                return;
            }

            string invoiceUuid = session.InvoiceUuid?.Trim()
                ?? throw new InvalidOperationException($"Missing invoice UUID for payment session {indexGuid}.");
            string spaceDomain = session.SpaceUuid?.Trim()
                ?? throw new InvalidOperationException($"Missing space domain for payment session {indexGuid}.");

            string accessToken = await GetAccessTokenAsync(cancellationToken);
            string archieStatus = MapArchieStatus(normalizedStatus);
            PaymentAccount? payment = await GetPaymentChannelAsync(spaceDomain, accessToken, cancellationToken) ?? throw new HttpRequestException($"No Xendit Payment Account found");
            var client = _httpClientFactory.CreateClient("Archie");
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"v1/spaces/{Uri.EscapeDataString(spaceDomain)}/invoices/{Uri.EscapeDataString(invoiceUuid)}/pay");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = JsonContent.Create(new
            {
                status = archieStatus,
                payment_account = new
                {
                    creation_date = payment.CreationDate,
                    href = payment.Href,
                    name = payment.Name,
                    type = payment.Type,
                    update_date = payment.UpdateDate,
                    uuid = payment.Uuid
                },
                amount=session.Amount
            });

            using var response = await client.SendAsync(request, cancellationToken);
            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Archie pay API failed with {(int)response.StatusCode}: {Truncate(responseBody, 2000)}");
            }

            await _paymentDataStore.MarkArchieNotifiedAsync(indexGuid, normalizedStatus, cancellationToken);
            _logger.LogInformation(
                "Archie payment status sent successfully. IndexGuid: {IndexGuid}, InvoiceUuid: {InvoiceUuid}, Status: {Status}",
                indexGuid,
                invoiceUuid,
                normalizedStatus);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        string clientId = GetRequiredConfiguration("Archie:ClientId");
        string clientSecret = GetRequiredConfiguration("Archie:ClientSecret");

        var client = _httpClientFactory.CreateClient("Archie");
        using var response = await client.PostAsJsonAsync(
            "v1/authenticate",
            new
            {
                client_id = clientId,
                client_secret = clientSecret
            },
            cancellationToken);

        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Archie auth token API failed with {(int)response.StatusCode}: {Truncate(responseBody, 2000)}");
        }

        using JsonDocument document = JsonDocument.Parse(responseBody);
        JsonElement root = document.RootElement;
        if (TryGetString(root, "accessToken", out var accessToken) ||
            TryGetString(root, "access_token", out accessToken) ||
            TryGetString(root, "token", out accessToken))
        {
            return accessToken!;
        }

        throw new InvalidOperationException("Archie auth token response did not contain an access token.");
    }

    private async Task<PaymentAccount?> GetPaymentChannelAsync(string spaceDomain, string accessToken, CancellationToken cancellationToken)
    {

        var client = _httpClientFactory.CreateClient("Archie");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/spaces/{spaceDomain}/paymentAccounts");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, cancellationToken);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Archie auth token API failed with {(int)response.StatusCode}: {Truncate(responseBody, 2000)}");
        }
        List<PaymentAccount>? payments = JsonSerializer.Deserialize<List<PaymentAccount>>(responseBody);
        if (payments != null)
            return payments.FirstOrDefault(x => x.Name == "Xendit");
        throw new InvalidOperationException("Archie auth token response did not contain an access token.");
    }

    private string GetRequiredConfiguration(string key)
    {
        string? value = _configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing configuration value '{key}'.");
        }

        return value.Trim();
    }

    private static string NormalizePaymentStatus(string paymentStatus)
    {
        return paymentStatus.Trim().Equals("Success", StringComparison.OrdinalIgnoreCase)
            ? "Success"
            : "Failed";
    }

    private static string MapArchieStatus(string paymentStatus)
    {
        return paymentStatus.Equals("Success", StringComparison.OrdinalIgnoreCase)
            ? "succeeded"
            : "failed";
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
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
