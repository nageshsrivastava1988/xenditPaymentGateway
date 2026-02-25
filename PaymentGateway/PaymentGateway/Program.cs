using PaymentGateway.Helpers;
using PaymentGateway.Services;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

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
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
var logDirectory = builder.Configuration["Logging:File:Path"] ?? Path.Combine(AppContext.BaseDirectory, "logs");
if (!Path.IsPathRooted(logDirectory))
{
    logDirectory = Path.Combine(builder.Environment.ContentRootPath, logDirectory);
}
var fileMinLevel = Enum.TryParse(builder.Configuration["Logging:File:MinLevel"], true, out LogLevel parsedLevel)
    ? parsedLevel
    : LogLevel.Information;
builder.Logging.AddProvider(new FileLoggerProvider(logDirectory, fileMinLevel));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapGet("/", () => Results.Redirect("/account/login"));

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Login}/{id?}")
    .WithStaticAssets();


app.Run();
