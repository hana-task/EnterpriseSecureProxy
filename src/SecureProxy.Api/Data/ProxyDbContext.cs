using Microsoft.EntityFrameworkCore;
using SecureProxy.Api.Data.Entities;
using SecureProxy.Api.Data.Seed;

namespace SecureProxy.Api.Data;

/// <summary>
/// The proxy database: configuration tables (what is allowed) and the audit table (what happened).
/// Table mappings live in Data/Configurations (one class per table); sample data in Data/Seed.
///
/// Configuration tables are SQL Server *temporal tables*: SQL Server automatically keeps every
/// previous version of every row, with the time range it was valid. This is our configuration
/// history (assignment 5.7). No secrets are stored in these tables, so none reach the history.
/// </summary>
public class ProxyDbContext : DbContext
{
    public ProxyDbContext(DbContextOptions<ProxyDbContext> options) : base(options)
    {
    }

    public DbSet<Destination> Destinations => Set<Destination>();
    public DbSet<Operation> Operations => Set<Operation>();
    public DbSet<ParameterRule> ParameterRules => Set<ParameterRule>();
    public DbSet<HeaderRule> HeaderRules => Set<HeaderRule>();
    public DbSet<EncryptionProfile> EncryptionProfiles => Set<EncryptionProfile>();
    public DbSet<OperationalSetting> OperationalSettings => Set<OperationalSetting>();
    public DbSet<RequestLog> RequestLogs => Set<RequestLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Picks up every IEntityTypeConfiguration class in this project (Data/Configurations).
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ProxyDbContext).Assembly);

        SeedData.Apply(modelBuilder);
    }
}
