var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Host.UseSerilog((context, _, loggerConfiguration) =>
{
    string logDirectory = context.Configuration["Logging:File:Path"] ?? Path.Combine(AppContext.BaseDirectory, "logs");
    if (!Path.IsPathRooted(logDirectory))
    {
        logDirectory = Path.Combine(context.HostingEnvironment.ContentRootPath, logDirectory);
    }

    Directory.CreateDirectory(logDirectory);

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
            outputTemplate: "{Timestamp:O} [{Level:u3}] {SourceContext} | {Message:lj}{NewLine}{Exception}"));
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
