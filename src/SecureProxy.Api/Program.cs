using Microsoft.EntityFrameworkCore;
using SecureProxy.Api.Data;
using SecureProxy.Api.Data.Repositories;
using SecureProxy.Api.Errors;
using SecureProxy.Api.Forwarding;
using SecureProxy.Api.Policies;
using SecureProxy.Api.Proxy;
using SecureProxy.Api.Security.Encryption;
using SecureProxy.Api.Validation;

var builder = WebApplication.CreateBuilder(args);

// The connection string is never in appsettings: user-secrets in development,
// environment variable ConnectionStrings__ProxyDb elsewhere. Fail at startup if it is missing.
var connectionString = builder.Configuration.GetConnectionString("ProxyDb")
    ?? throw new InvalidOperationException("Connection string 'ProxyDb' is not configured.");
builder.Services.AddDbContext<ProxyDbContext>(options => options.UseSqlServer(connectionString));

// Configuration reading. Lifetimes matter:
// - the repository uses the DbContext, which is per request (scoped), so it is scoped too;
// - the cache must be shared by all requests, so it is a singleton;
// - the provider uses the repository, so it is scoped. (A singleton holding a scoped
//   DbContext would keep one DbContext alive for the whole app: a "captive dependency" bug.)
builder.Services.AddScoped<IPolicyRepository, PolicyRepository>();
builder.Services.AddSingleton<PolicyCache>();
builder.Services.AddScoped<IPolicyProvider, PolicyProvider>();

// Checks before forwarding. The key source and decryptor hold no per-request state (singletons);
// the validator uses the scoped policy provider, so it is scoped too.
builder.Services.AddSingleton<IKeySource, ConfigurationKeySource>();
builder.Services.AddSingleton<AesGcmPayloadDecryptor>();
builder.Services.AddScoped<RequestValidator>();

// Forwarding. One named client for all destinations:
// - Timeout is infinite here because the forwarder applies each destination's own timeout;
// - redirects are not followed, so a destination cannot send the proxy to another host (SSRF).
builder.Services.AddHttpClient(UpstreamForwarder.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<UpstreamForwarder>();

// The whole flow of one request. Scoped because the validator it uses is scoped.
builder.Services.AddScoped<ProxyService>();

// Unexpected exceptions become a generic 500 in the usual error format (see UseExceptionHandler below).
builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// First in the pipeline, so it catches exceptions from everything after it.
// The empty lambda only satisfies a .NET 8 startup check; the registered UnhandledExceptionHandler does the work.
app.UseExceptionHandler(_ => { });

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
