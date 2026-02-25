namespace PaymentGateway.Models;

public sealed class UserAccountRecord
{
    public Guid UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public byte[] PasswordHash { get; set; } = [];
    public byte[] PasswordSalt { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
