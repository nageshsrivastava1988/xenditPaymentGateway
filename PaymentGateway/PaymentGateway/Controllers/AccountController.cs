using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using PaymentGateway.Helpers;
using PaymentGateway.Services;

namespace PaymentGateway.Controllers;

public class AccountController : Controller
{
    private readonly ILogger<AccountController> _logger;
    private readonly IPaymentDataStore _dataStore;
    private readonly IAccountEmailService _accountEmailService;
    private readonly IConfiguration _configuration;

    public AccountController(
        ILogger<AccountController> logger,
        IPaymentDataStore dataStore,
        IAccountEmailService accountEmailService,
        IConfiguration configuration)
    {
        _logger = logger;
        _dataStore = dataStore;
        _accountEmailService = accountEmailService;
        _configuration = configuration;
    }

    [AllowAnonymous]
    [HttpGet("account/login")]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Report");
        }

        ViewData["ReturnUrl"] = returnUrl;
        return View(new LoginViewModel());
    }

    [AllowAnonymous]
    [HttpPost("account/login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl, CancellationToken cancellationToken)
    {
        ViewData["ReturnUrl"] = returnUrl;
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        UserAccountRecord? user = await _dataStore.GetUserByEmailAsync(model.Email, cancellationToken);
        bool firstUserCreated = false;
        if (user is null)
        {
            (byte[] initialHash, byte[] initialSalt) = PasswordHasher.HashPassword(model.Password);
            user = await _dataStore.CreateFirstUserIfNoUsersAsync(
                model.Email,
                model.Email,
                initialHash,
                initialSalt,
                cancellationToken);

            firstUserCreated = user is not null;
        }
        else if (!PasswordHasher.VerifyPassword(model.Password, user.PasswordHash, user.PasswordSalt))
        {
            user = null;
        }

        if (user is null)
        {
            ModelState.AddModelError(string.Empty, "Invalid email or password.");
            return View(model);
        }

        await SignInUserAsync(user, model.RememberMe);

        if (firstUserCreated)
        {
            return RedirectToAction("Index", "Report");
        }

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction("Index", "Report");
    }

    [Authorize]
    [HttpPost("account/logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [AllowAnonymous]
    [HttpGet("account/forgot-password")]
    public IActionResult ForgotPassword()
    {
        return View(new ForgotPasswordViewModel());
    }

    [AllowAnonymous]
    [HttpPost("account/forgot-password")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        UserAccountRecord? user = await _dataStore.GetUserByEmailAsync(model.Email, cancellationToken);
        if (user is not null)
        {
            try
            {
                (string rawToken, byte[] tokenHash) = ResetTokenHelper.CreateToken();
                int expiryMinutes = int.TryParse(_configuration["Auth:ResetTokenExpiryMinutes"], out int parsedMinutes)
                    ? parsedMinutes
                    : 30;
                Guid tokenId = await _dataStore.CreatePasswordResetTokenAsync(
                    user.UserId,
                    tokenHash,
                    DateTime.UtcNow.AddMinutes(Math.Clamp(expiryMinutes, 5, 240)),
                    cancellationToken);

                string resetLink = Url.Action(
                                       nameof(ResetPassword),
                                       "Account",
                                       new { tokenId, token = rawToken },
                                       Request.Scheme)
                                   ?? $"{Request.Scheme}://{Request.Host}/account/reset-password?tokenId={tokenId}&token={Uri.EscapeDataString(rawToken)}";

                await _accountEmailService.SendPasswordResetLinkAsync(user.Email, resetLink, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate/send password reset link for user {UserId}", user.UserId);
            }
        }

        return RedirectToAction(nameof(ForgotPasswordConfirmation));
    }

    [AllowAnonymous]
    [HttpGet("account/forgot-password-confirmation")]
    public IActionResult ForgotPasswordConfirmation()
    {
        return View();
    }

    [AllowAnonymous]
    [HttpGet("account/reset-password")]
    public async Task<IActionResult> ResetPassword(Guid tokenId, string token, CancellationToken cancellationToken)
    {
        if (tokenId == Guid.Empty || string.IsNullOrWhiteSpace(token))
        {
            return View("ResetPasswordInvalid");
        }

        bool isValid = await _dataStore.IsPasswordResetTokenValidAsync(tokenId, ResetTokenHelper.HashToken(token), cancellationToken);
        if (!isValid)
        {
            return View("ResetPasswordInvalid");
        }

        return View(new ResetPasswordViewModel
        {
            TokenId = tokenId,
            Token = token
        });
    }

    [AllowAnonymous]
    [HttpPost("account/reset-password")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        (byte[] newHash, byte[] newSalt) = PasswordHasher.HashPassword(model.NewPassword);
        bool updated = await _dataStore.TryResetPasswordWithTokenAsync(
            model.TokenId,
            ResetTokenHelper.HashToken(model.Token),
            newHash,
            newSalt,
            cancellationToken);

        if (!updated)
        {
            ModelState.AddModelError(string.Empty, "Reset link is invalid, expired, or already used.");
            return View(model);
        }

        return RedirectToAction(nameof(ResetPasswordConfirmation));
    }

    [AllowAnonymous]
    [HttpGet("account/reset-password-confirmation")]
    public IActionResult ResetPasswordConfirmation()
    {
        return View();
    }

    [Authorize]
    [HttpGet("account/change-password")]
    public IActionResult ChangePassword()
    {
        return View(new ChangePasswordViewModel());
    }

    [Authorize]
    [HttpPost("account/change-password")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        string? userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdClaim, out Guid userId))
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction(nameof(Login));
        }

        UserAccountRecord? user = await _dataStore.GetUserByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction(nameof(Login));
        }

        if (!PasswordHasher.VerifyPassword(model.CurrentPassword, user.PasswordHash, user.PasswordSalt))
        {
            ModelState.AddModelError(nameof(ChangePasswordViewModel.CurrentPassword), "Current password is incorrect.");
            return View(model);
        }

        if (model.CurrentPassword == model.NewPassword)
        {
            ModelState.AddModelError(nameof(ChangePasswordViewModel.NewPassword), "New password must be different from current password.");
            return View(model);
        }

        (byte[] newHash, byte[] newSalt) = PasswordHasher.HashPassword(model.NewPassword);
        await _dataStore.UpdateUserPasswordAsync(userId, newHash, newSalt, cancellationToken);

        TempData["SuccessMessage"] = "Password updated successfully.";
        return RedirectToAction(nameof(ChangePassword));
    }

    private async Task SignInUserAsync(UserAccountRecord user, bool rememberMe)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName),
            new(ClaimTypes.Email, user.Email)
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        var authProperties = new AuthenticationProperties
        {
            IsPersistent = rememberMe,
            ExpiresUtc = rememberMe
                ? DateTimeOffset.UtcNow.AddDays(7)
                : DateTimeOffset.UtcNow.AddHours(8)
        };

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, authProperties);
    }
}
