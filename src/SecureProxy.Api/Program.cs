using Microsoft.EntityFrameworkCore;
using SecureProxy.Api.Data;
using SecureProxy.Api.Data.Repositories;
using SecureProxy.Api.Policies;

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

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

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
