using Microsoft.AspNetCore.Authorization;
using PaymentGateway.Models;
using PaymentGateway.Services;

namespace PaymentGateway.Controllers;

[Authorize]
public class ReportController : Controller
{
    private readonly ILogger<ReportController> _logger;
    private readonly IPaymentDataStore _dataStore;

    public ReportController(ILogger<ReportController> logger, IPaymentDataStore dataStore)
    {
        _logger = logger;
        _dataStore = dataStore;
    }

    [HttpGet("report")]
    public async Task<IActionResult> Index(
        DateTime? fromDateTime,
        DateTime? toDateTime,
        string? status,
        string? referenceNo,
        int page = 1,
        int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        bool isAllPageSize = pageSize <= 0;
        int safePage = isAllPageSize ? 1 : Math.Max(1, page);
        int safePageSize = isAllPageSize ? 0 : Math.Clamp(pageSize, 10, 100);
        int queryPageSize = isAllPageSize ? 0 : safePageSize;
        DateTime? fromUtc = ConvertLocalToUtc(fromDateTime);
        DateTime? toUtc = ConvertLocalToUtc(toDateTime);

        PaymentReportQueryResult result = await _dataStore.SearchPaymentSessionsAsync(
            fromUtc,
            toUtc,
            status,
            referenceNo,
            safePage,
            queryPageSize,
            cancellationToken);
        _logger.LogInformation("Report page rendered. SessionCount: {Count}, TotalCount: {TotalCount}", result.Sessions.Count, result.TotalCount);

        return View(new ReportPageViewModel
        {
            Sessions = result.Sessions,
            FromDateTime = fromDateTime,
            ToDateTime = toDateTime,
            Status = string.IsNullOrWhiteSpace(status) ? null : status.Trim(),
            ReferenceNo = string.IsNullOrWhiteSpace(referenceNo) ? null : referenceNo.Trim(),
            PageNumber = safePage,
            PageSize = safePageSize,
            TotalCount = result.TotalCount
        });
    }

    private static DateTime? ConvertLocalToUtc(DateTime? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        DateTime localValue = value.Value;
        if (localValue.Kind == DateTimeKind.Unspecified)
        {
            localValue = DateTime.SpecifyKind(localValue, DateTimeKind.Local);
        }

        return localValue.ToUniversalTime();
    }
}
