const string SerilogOutputTemplate = "{Timestamp:O} [{Level:u3}] {SourceContext} | {Message:lj}{NewLine}{Exception}";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
Serilog.Debugging.SelfLog.Enable(message => Console.Error.WriteLine($"[Serilog] {message}"));
builder.Host.UseSerilog((context, _, loggerConfiguration) =>
{
    string logDirectory = ResolveWritableLogDirectory(
        context.Configuration["Logging:File:Path"],
        context.HostingEnvironment.ContentRootPath);

    var fileMinLevel = ParseLogEventLevel(context.Configuration["Logging:File:MinLevel"], LogEventLevel.Information);
    var microsoftMinLevel = ParseLogEventLevel(context.Configuration["Logging:LogLevel:Microsoft"], LogEventLevel.Warning);
    var aspNetCoreMinLevel = ParseLogEventLevel(context.Configuration["Logging:LogLevel:Microsoft.AspNetCore"], LogEventLevel.Warning);

    loggerConfiguration
        .MinimumLevel.Is(fileMinLevel)
        .MinimumLevel.Override("Microsoft", microsoftMinLevel)
        .MinimumLevel.Override("Microsoft.AspNetCore", aspNetCoreMinLevel)
        .Enrich.FromLogContext()
        .WriteTo.Async(writeTo => writeTo.File(
            path: Path.Combine(logDirectory, "app-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 31,
            shared: true,
            outputTemplate: SerilogOutputTemplate));
});

builder.WebHost.ConfigureKestrel((context, options) =>
{
    if (context.HostingEnvironment.IsDevelopment())
    {
        options.ListenAnyIP(7532);
    }
    else
    {
        options.ListenUnixSocket("/tmp/paymentgateway.sock");
    }
});

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddSingleton<IPaymentDataStore, PaymentDataStore>();
builder.Services.AddSingleton<IAccountEmailService, AccountEmailService>();
builder.Services.AddSingleton<IArchiePaymentNotifier, ArchiePaymentNotifier>();
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/account/login";
        options.AccessDeniedPath = "/account/login";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
    });
builder.Services.AddHttpClient("Xendit", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Xendit:BaseUrl"] ?? "https://api.xendit.co/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient("Archie", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Archie:BaseUrl"] ?? "https://api.archieapp.co/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

//app.UseHttpsRedirection();
app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("TraceIdentifier", httpContext.TraceIdentifier);
        diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value ?? string.Empty);
        diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme);
    };
});
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapGet("/", () => Results.Redirect("/account/login"));

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Login}/{id?}")
    .WithStaticAssets();

app.Logger.LogInformation("{ProjectBanner}", BuildProjectBanner(app.Environment.EnvironmentName));

try
{
    await app.RunAsync();
}
finally
{
    await Log.CloseAndFlushAsync();
}

static LogEventLevel ParseLogEventLevel(string? value, LogEventLevel fallback)
{
    return Enum.TryParse<LogEventLevel>(value, true, out var parsedLevel)
        ? parsedLevel
        : fallback;
}

static string ResolveWritableLogDirectory(string? configuredPath, string contentRootPath)
{
    string normalizedConfiguredPath = string.IsNullOrWhiteSpace(configuredPath)
        ? "logs"
        : configuredPath.Trim();
    string appBaseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    string normalizedContentRoot = contentRootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var candidates = new List<string>();

    if (Path.IsPathRooted(normalizedConfiguredPath))
    {
        candidates.Add(normalizedConfiguredPath);
    }
    else
    {
        candidates.Add(Path.Combine(appBaseDirectory, normalizedConfiguredPath));
        if (!string.Equals(appBaseDirectory, normalizedContentRoot, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(Path.Combine(normalizedContentRoot, normalizedConfiguredPath));
        }
    }

    candidates.Add(Path.Combine(Path.GetTempPath(), "paymentgateway-logs"));

    Exception? lastError = null;
    foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        try
        {
            Directory.CreateDirectory(candidate);

            string probeFilePath = Path.Combine(candidate, $".write-test-{Environment.ProcessId}");
            using (File.Create(probeFilePath, 1, FileOptions.DeleteOnClose))
            {
            }

            if (!string.Equals(candidate, candidates[0], StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"[Logging] Falling back to writable log directory: {candidate}");
            }

            return candidate;
        }
        catch (Exception ex)
        {
            lastError = ex;
            Console.Error.WriteLine($"[Logging] Failed to use log directory '{candidate}': {ex.Message}");
        }
    }

    throw new InvalidOperationException("No writable log directory is available for Serilog file logging.", lastError);
}

static string BuildProjectBanner(string environmentName)
{
    return $$"""

    
    ██████╗  █████╗ ██╗   ██╗███╗   ███╗███████╗███╗   ██╗████████╗
    ██╔══██╗██╔══██╗╚██╗ ██╔╝████╗ ████║██╔════╝████╗  ██║╚══██╔══╝
    ██████╔╝███████║ ╚████╔╝ ██╔████╔██║█████╗  ██╔██╗ ██║   ██║
    ██╔═══╝ ██╔══██║  ╚██╔╝  ██║╚██╔╝██║██╔══╝  ██║╚██╗██║   ██║
    ██║     ██║  ██║   ██║   ██║ ╚═╝ ██║███████╗██║ ╚████║   ██║
    ╚═╝     ╚═╝  ╚═╝   ╚═╝   ╚═╝     ╚═╝╚══════╝╚═╝  ╚═══╝   ╚═╝

     ██████╗  █████╗ ████████╗███████╗██╗    ██╗ █████╗ ██╗   ██╗
    ██╔════╝ ██╔══██╗╚══██╔══╝██╔════╝██║    ██║██╔══██╗╚██╗ ██╔╝
    ██║  ███╗███████║   ██║   █████╗  ██║ █╗ ██║███████║ ╚████╔╝
    ██║   ██║██╔══██║   ██║   ██╔══╝  ██║███╗██║██╔══██║  ╚██╔╝
    ╚██████╔╝██║  ██║   ██║   ███████╗╚███╔███╔╝██║  ██║   ██║
     ╚═════╝ ╚═╝  ╚═╝   ╚═╝   ╚══════╝ ╚══╝╚══╝ ╚═╝  ╚═╝   ╚═╝

    Environment: {{environmentName}}
    Started At : {{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}}
    """;
}
