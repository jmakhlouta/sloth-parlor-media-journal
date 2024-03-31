using System.IdentityModel.Tokens.Jwt;
using Azure.Identity;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using SlothParlor.MediaJournal.WebApp.Components;
using AppConstants = SlothParlor.MediaJournal.WebApp.Constants;

// Must be set before OpenIdConnectOptions are configured
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

var builder = WebApplication.CreateBuilder(args);

// Additional configuration sources
if (builder.Configuration.GetValue<Uri>("AzureKeyVault:Uri") is Uri keyVaultUri)
{
    builder.Configuration
        .AddAzureKeyVault(keyVaultUri, new DefaultAzureCredential());
}

var applicationOrigins = builder.Configuration
    .GetSection("HttpHost:Origins:ApplicationOrigins")
    .Get<string[]>() ?? [];

if (applicationOrigins.Length == 0)
{
    throw new ArgumentException("ApplicationOrigins must be set in the configuration.", nameof(applicationOrigins));
}

// Configure CORS
builder.Services.AddCors(options =>
{
    var trustedOrigins = new List<string>();

    trustedOrigins.AddRange(applicationOrigins);
    trustedOrigins.AddRange(builder.Configuration
        .GetSection("HttpHost:Origins:IdentityOrigins")
        .Get<string[]>() ?? []);

    options.AddDefaultPolicy(policyBuilder =>
    {
        policyBuilder.WithOrigins([.. trustedOrigins])
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// Configure proxy
if (builder.Configuration.GetValue<bool>("HttpHost:UseForwardedHeaders"))
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        options.RequireHeaderSymmetry = false;

        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();

        options.AllowedHosts.Clear();
        foreach (var origin in applicationOrigins)
        {
            var uri = new Uri(origin);
            var host = uri.Host;
            options.AllowedHosts.Add(host);
        }
    });
}

// Configure Identity
var msGraphConfigSection = builder.Configuration.GetSection("MicrosoftGraphApi");
var msGraphScopes = msGraphConfigSection["Scopes"]?.Split(' ');

builder.Services.AddMicrosoftIdentityWebAppAuthentication(builder.Configuration, Constants.AzureAdB2C)
    .EnableTokenAcquisitionToCallDownstreamApi(msGraphScopes)
        .AddDownstreamApi("MicrosoftGraphApi", msGraphConfigSection)
        .AddInMemoryTokenCaches();

// Configure webapp services
builder.Services
    .AddRazorPages()
    .AddMicrosoftIdentityUI();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
        .AddMicrosoftIdentityConsentHandler()
    .AddInteractiveWebAssemblyComponents();

builder.Services
    .AddCascadingAuthenticationState();

var appDbConnectionString = builder.Configuration.GetConnectionString(AppConstants.AppDbConnectionStringKey);
ArgumentException.ThrowIfNullOrWhiteSpace(appDbConnectionString);

builder.Services.AddDbContext<MediaJournalDbContext>(options =>
{
    options.UseNpgsql(appDbConnectionString);

    if (builder.Environment.IsDevelopment())
    {
        options
            .EnableDetailedErrors()
            .EnableSensitiveDataLogging();
    }
});

var app = builder.Build();

if (builder.Configuration.GetValue<bool>("HttpHost:UseForwardedHeaders"))
{
    app.UseForwardedHeaders();
}

if (builder.Configuration["HttpHost:BasePath"] is string basePath && !string.IsNullOrWhiteSpace(basePath))
{
    app.UsePathBase(basePath);
}

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseCors();

app.UseRouting();

app.UseStaticFiles();
app.UseAntiforgery();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapRazorPages();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(SlothParlor.MediaJournal.WebApp.Client._Imports).Assembly);

app.Run();