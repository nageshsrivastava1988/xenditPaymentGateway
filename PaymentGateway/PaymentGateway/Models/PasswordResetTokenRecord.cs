namespace PaymentGateway.Models;

public sealed class PasswordResetTokenRecord
{
    public Guid TokenId { get; set; }
    public Guid UserId { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? UsedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
