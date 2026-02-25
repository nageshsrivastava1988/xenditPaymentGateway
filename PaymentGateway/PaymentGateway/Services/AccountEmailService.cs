using System.Net;
using System.Net.Mail;

namespace PaymentGateway.Services;

public interface IAccountEmailService
{
    Task SendPasswordResetLinkAsync(string toEmail, string resetLink, CancellationToken cancellationToken);
}

public sealed class AccountEmailService : IAccountEmailService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AccountEmailService> _logger;

    public AccountEmailService(IConfiguration configuration, ILogger<AccountEmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendPasswordResetLinkAsync(string toEmail, string resetLink, CancellationToken cancellationToken)
    {
        string? smtpHost = _configuration["Email:SmtpHost"];
        string? fromEmail = _configuration["Email:From"];

        if (string.IsNullOrWhiteSpace(smtpHost) || string.IsNullOrWhiteSpace(fromEmail))
        {
            _logger.LogWarning(
                "SMTP is not fully configured. Reset link for {Email}: {ResetLink}",
                toEmail,
                resetLink);
            return;
        }

        int port = int.TryParse(_configuration["Email:SmtpPort"], out int parsedPort) ? parsedPort : 587;
        bool useSsl = bool.TryParse(_configuration["Email:UseSsl"], out bool parsedSsl) ? parsedSsl : true;
        string? username = _configuration["Email:Username"];
        string? password = _configuration["Email:Password"];

        using var message = new MailMessage(fromEmail, toEmail)
        {
            Subject = "Reset your password",
            Body = $"Use this one-time link to reset your password: {resetLink}",
            IsBodyHtml = false
        };

        using var smtp = new SmtpClient(smtpHost, port)
        {
            EnableSsl = useSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false
        };

        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
        {
            smtp.Credentials = new NetworkCredential(username, password);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await smtp.SendMailAsync(message, cancellationToken);
        _logger.LogInformation("Password reset email sent to {Email}", toEmail);
    }
}
