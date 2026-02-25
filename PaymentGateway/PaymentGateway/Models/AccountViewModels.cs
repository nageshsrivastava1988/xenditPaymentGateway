using System.ComponentModel.DataAnnotations;

namespace PaymentGateway.Models;

public sealed class LoginViewModel
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public bool RememberMe { get; set; }
}

public sealed class ForgotPasswordViewModel
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;
}

public sealed class ResetPasswordViewModel
{
    [Required]
    public Guid TokenId { get; set; }

    [Required]
    public string Token { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    [DataType(DataType.Password)]
    public string NewPassword { get; set; } = string.Empty;

    [Required]
    [DataType(DataType.Password)]
    [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public sealed class ChangePasswordViewModel
{
    [Required]
    [DataType(DataType.Password)]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    [DataType(DataType.Password)]
    public string NewPassword { get; set; } = string.Empty;

    [Required]
    [DataType(DataType.Password)]
    [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public sealed class ReportPageViewModel
{
    public List<PaymentSessionRecord> Sessions { get; set; } = [];
    public DateTime? FromDateTime { get; set; }
    public DateTime? ToDateTime { get; set; }
    public string? Status { get; set; }
    public string? ReferenceNo { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public int TotalCount { get; set; }
    public int EffectivePageSize =>
        PageSize <= 0
            ? Math.Max(1, TotalCount == 0 ? 1 : TotalCount)
            : Math.Max(1, PageSize);
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)EffectivePageSize));
    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < TotalPages;

    public string FromDateTimeInput => FromDateTime?.ToString("yyyy-MM-ddTHH:mm") ?? string.Empty;
    public string ToDateTimeInput => ToDateTime?.ToString("yyyy-MM-ddTHH:mm") ?? string.Empty;
}

public sealed class PaymentReportQueryResult
{
    public List<PaymentSessionRecord> Sessions { get; set; } = [];
    public int TotalCount { get; set; }
}
