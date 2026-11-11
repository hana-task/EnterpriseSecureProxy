using Microsoft.EntityFrameworkCore;
using SecureProxy.Api.Admin;
using SecureProxy.Api.Audit;
using SecureProxy.Api.Data;
using SecureProxy.Api.Data.Repositories;
using SecureProxy.Api.Errors;
using SecureProxy.Api.Forwarding;
using SecureProxy.Api.Policies;
using SecureProxy.Api.Proxy;
using SecureProxy.Api.Security.Encryption;
using SecureProxy.Api.Validation;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// The technical log (decision 12): Serilog, configured in appsettings.json ("Serilog" section).
// Console for people, a daily JSON file for tools. FromLogContext adds the scope values, such as the
// CorrelationId of CorrelationIdMiddleware, to every line. Not the audit log: that is RequestLogs in SQL Server.
builder.Host.UseSerilog((context, services, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// Do not announce the server software ("Server: Kestrel") in every response.
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

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
// - the network handler follows no redirects and adds no tracing headers (see CreatePrimaryHandler).
builder.Services.AddHttpClient(UpstreamForwarder.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(UpstreamForwarder.CreatePrimaryHandler);
builder.Services.AddSingleton<UpstreamForwarder>();

// The audit record of one request, kept in RequestLogs. Scoped: one record per request, and it uses the DbContext.
builder.Services.AddScoped<RequestAuditor>();

// The whole flow of one request. Scoped because the validator and the auditor it uses are scoped.
builder.Services.AddScoped<ProxyService>();

// The admin API key check (the admin controller uses it through [ServiceFilter]). No per-request state.
builder.Services.AddSingleton<AdminKeyFilter>();

// Unexpected exceptions become a generic 500 in the usual error format (see UseExceptionHandler below).
builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// First of all: the correlation id must be set before anything logs or answers, including the
// exception handler below (its 500 answer carries the id too).
app.UseMiddleware<CorrelationIdMiddleware>();

// One summary line per request: method, path (no query string), status, duration. No headers, no body.
// Placed before the exception handler, so for an unexpected exception it logs the final 500.
app.UseSerilogRequestLogging();

// Next, so it catches exceptions from everything after it.
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

// Makes the compiler-generated Program class public, so the integration tests can start this app
// with WebApplicationFactory<Program>. It adds no behaviour.
public partial class Program { }
