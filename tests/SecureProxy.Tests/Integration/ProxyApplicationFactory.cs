using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SecureProxy.Api.Data;
using SecureProxy.Api.Data.Entities;
using SecureProxy.Api.Forwarding;
using SecureProxy.Tests.Forwarding;

namespace SecureProxy.Tests.Integration;

/// <summary>
/// Starts the real application (the real Program.cs) in memory, on a TestServer, and changes only two things:
/// 1. the database: the same SQL Server, but a separate database that is rebuilt on every test run;
/// 2. the destination: the "upstream" HttpClient talks to a RecordingHandler instead of the network.
/// Everything else (controller, checks, cache, forwarder, error handling) is the production code.
///
/// xUnit creates one instance for the whole test class (IClassFixture), so the app and the database
/// are built once, not once per test.
/// </summary>
public sealed class ProxyApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestDatabaseName = "SecureProxy_Tests";

    /// <summary>The fake destination. Every request the proxy forwards ends up here and is recorded.</summary>
    public RecordingHandler Destination { get; } = new(HttpStatusCode.OK);

    /// <summary>
    /// The AES-256 key for "payments-v1", the key reference of payment.create in the seed. A fixed test value,
    /// so the tests do not depend on the developer's own secret. The tests use it to encrypt like a caller would.
    /// </summary>
    public static readonly byte[] PaymentsKey = Enumerable.Range(1, 32).Select(number => (byte)number).ToArray();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Added after the app's own configuration sources, so it wins over a key in the user-secrets.
        // ConfigurationKeySource reads "ProxyKeys:{reference}".
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ProxyKeys:payments-v1"] = Convert.ToBase64String(PaymentsKey) }));

        // ConfigureTestServices runs after all the registrations in Program.cs, so what is registered
        // here replaces what Program registered.
        builder.ConfigureTestServices(services =>
        {
            // 1. Database: remove the options Program built (development database) and register the test database.
            services.RemoveAll<DbContextOptions<ProxyDbContext>>();
            services.AddDbContext<ProxyDbContext>(options => options.UseSqlServer(TestConnectionString()));

            // 2. Destination: Program registered the "upstream" client with a SocketsHttpHandler (real network).
            //    Configuring the same client name again adds a second setting that runs after the first one,
            //    so the last one decides the primary handler: requests now go to the RecordingHandler.
            services.AddHttpClient(UpstreamForwarder.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Destination);
        });
    }

    /// <summary>Runs once, when the app is first started: gives every test run a fresh database.</summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxyDbContext>();
        db.Database.EnsureDeleted(); // the test database only: the DbContext was replaced above
        db.Database.Migrate();       // the real migration, so the seed data is there too
        AddTestOperations(db);

        return host;
    }

    /// <summary>What the tests need and the seed does not have.</summary>
    private static void AddTestOperations(ProxyDbContext db)
    {
        // A text parameter in the path (the seed only has numbers there, which never reach the
        // path traversal check), and a header that is allowed here but on the fixed forbidden list.
        db.Operations.Add(new Operation
        {
            Key = "test.document.get", UrlId = "test-documents", DestinationId = 1, HttpMethod = "GET",
            PathTemplate = "/documents/{documentName}", IsEnabled = true, MaxRequestBytes = 4096,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            ParameterRules =
            {
                new ParameterRule { Name = "documentName", Location = ParameterLocation.Path,
                    DataType = ParameterDataType.String, IsRequired = true, MaxLength = 100 }
            },
            HeaderRules =
            {
                new HeaderRule { HeaderName = "Accept-Language" },
                new HeaderRule { HeaderName = "Authorization" }
            }
        });

        db.SaveChanges();
    }

    /// <summary>
    /// The developer's connection string (user-secrets, or the ConnectionStrings__ProxyDb environment
    /// variable) with only the database name changed, so the tests never touch the development data.
    /// </summary>
    private static string TestConnectionString()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("ProxyDb")
            ?? throw new InvalidOperationException("Connection string 'ProxyDb' is not configured.");

        return new SqlConnectionStringBuilder(connectionString) { InitialCatalog = TestDatabaseName }.ConnectionString;
    }
}
