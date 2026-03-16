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
            _logger.LogInformation("Authenticated user requested login page and was redirected to the report page.");
            return RedirectToAction("Index", "Report");
        }

        _logger.LogInformation("Login page rendered. ReturnUrl: {ReturnUrl}", returnUrl);
        ViewData["ReturnUrl"] = returnUrl;
        return View(new LoginViewModel());
    }

    [AllowAnonymous]
    [HttpPost("account/login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl, CancellationToken cancellationToken)
    {
        ViewData["ReturnUrl"] = returnUrl;
        _logger.LogInformation("Login attempt received. Email: {Email}, ReturnUrl: {ReturnUrl}, RememberMe: {RememberMe}", model.Email, returnUrl, model.RememberMe);
        if (!ModelState.IsValid)
        {
            _logger.LogWarning("Login attempt rejected because the model state is invalid. Email: {Email}", model.Email);
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
            _logger.LogWarning("Login attempt failed. Invalid credentials. Email: {Email}", model.Email);
            ModelState.AddModelError(string.Empty, "Invalid email or password.");
            return View(model);
        }

        await SignInUserAsync(user, model.RememberMe);
        _logger.LogInformation("Login attempt succeeded. UserId: {UserId}, Email: {Email}, RememberMe: {RememberMe}", user.UserId, user.Email, model.RememberMe);

        if (firstUserCreated)
        {
            _logger.LogInformation("First application user created during login. UserId: {UserId}, Email: {Email}", user.UserId, user.Email);
            return RedirectToAction("Index", "Report");
        }

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            _logger.LogInformation("Redirecting authenticated user to local return URL: {ReturnUrl}", returnUrl);
            return Redirect(returnUrl);
        }

        _logger.LogInformation("Redirecting authenticated user to the report page.");
        return RedirectToAction("Index", "Report");
    }

    [Authorize]
    [HttpPost("account/logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        _logger.LogInformation("Logout requested for User: {User}", User.Identity?.Name);
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [AllowAnonymous]
    [HttpGet("account/forgot-password")]
    public IActionResult ForgotPassword()
    {
        _logger.LogInformation("Forgot password page rendered.");
        return View(new ForgotPasswordViewModel());
    }

    [AllowAnonymous]
    [HttpPost("account/forgot-password")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            _logger.LogWarning("Forgot password submission rejected because the model state is invalid. Email: {Email}", model.Email);
            return View(model);
        }

        _logger.LogInformation("Forgot password requested. Email: {Email}", model.Email);
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
                _logger.LogInformation("Password reset token generated. UserId: {UserId}, Email: {Email}", user.UserId, user.Email);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate/send password reset link for user {UserId}", user.UserId);
            }
        }
        else
        {
            _logger.LogWarning("Forgot password requested for an unknown email. Email: {Email}", model.Email);
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
            _logger.LogWarning("Reset password page requested with missing token data. TokenId: {TokenId}", tokenId);
            return View("ResetPasswordInvalid");
        }

        bool isValid = await _dataStore.IsPasswordResetTokenValidAsync(tokenId, ResetTokenHelper.HashToken(token), cancellationToken);
        if (!isValid)
        {
            _logger.LogWarning("Reset password page requested with an invalid or expired token. TokenId: {TokenId}", tokenId);
            return View("ResetPasswordInvalid");
        }

        _logger.LogInformation("Reset password page rendered for a valid token. TokenId: {TokenId}", tokenId);
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
            _logger.LogWarning("Reset password submission rejected because the model state is invalid. TokenId: {TokenId}", model.TokenId);
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
            _logger.LogWarning("Reset password submission failed because the token is invalid, expired, or used. TokenId: {TokenId}", model.TokenId);
            ModelState.AddModelError(string.Empty, "Reset link is invalid, expired, or already used.");
            return View(model);
        }

        _logger.LogInformation("Password reset completed successfully. TokenId: {TokenId}", model.TokenId);
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
        _logger.LogInformation("Change password page rendered for User: {User}", User.Identity?.Name);
        return View(new ChangePasswordViewModel());
    }

    [Authorize]
    [HttpPost("account/change-password")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            _logger.LogWarning("Change password submission rejected because the model state is invalid. User: {User}", User.Identity?.Name);
            return View(model);
        }

        string? userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdClaim, out Guid userId))
        {
            _logger.LogWarning("Change password submission failed because the user identifier claim is invalid.");
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction(nameof(Login));
        }

        UserAccountRecord? user = await _dataStore.GetUserByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            _logger.LogWarning("Change password submission failed because the user record was not found. UserId: {UserId}", userId);
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction(nameof(Login));
        }

        if (!PasswordHasher.VerifyPassword(model.CurrentPassword, user.PasswordHash, user.PasswordSalt))
        {
            _logger.LogWarning("Change password submission failed because the current password is incorrect. UserId: {UserId}", userId);
            ModelState.AddModelError(nameof(ChangePasswordViewModel.CurrentPassword), "Current password is incorrect.");
            return View(model);
        }

        if (model.CurrentPassword == model.NewPassword)
        {
            _logger.LogWarning("Change password submission failed because the new password matches the current password. UserId: {UserId}", userId);
            ModelState.AddModelError(nameof(ChangePasswordViewModel.NewPassword), "New password must be different from current password.");
            return View(model);
        }

        (byte[] newHash, byte[] newSalt) = PasswordHasher.HashPassword(model.NewPassword);
        await _dataStore.UpdateUserPasswordAsync(userId, newHash, newSalt, cancellationToken);
        _logger.LogInformation("Password changed successfully. UserId: {UserId}", userId);

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
