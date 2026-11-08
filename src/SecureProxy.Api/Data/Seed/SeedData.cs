using Microsoft.EntityFrameworkCore;
using SecureProxy.Api.Data.Entities;

namespace SecureProxy.Api.Data.Seed;

/// <summary>
/// Sample configuration, inserted by the migration so a clean database is ready for a demo.
/// Each operation shows one scenario. Automated tests create their own data and do not rely on this.
/// The destination addresses are local placeholders.
/// </summary>
public static class SeedData
{
    private static readonly DateTime SeedTime = new(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc);

    public static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Destination>().HasData(
            new Destination
            {
                Id = 1, Name = "crm", BaseUrl = "http://localhost:5101", IsEnabled = true,
                TimeoutSeconds = 10, MaxResponseBytes = 1_048_576, CreatedAt = SeedTime, UpdatedAt = SeedTime
            },
            new Destination
            {
                Id = 2, Name = "payments", BaseUrl = "http://localhost:5102", IsEnabled = true,
                TimeoutSeconds = 15, MaxResponseBytes = 1_048_576, CreatedAt = SeedTime, UpdatedAt = SeedTime
            },
            // Disabled destination: invoice.get points here and must be rejected.
            new Destination
            {
                Id = 3, Name = "legacy-billing", BaseUrl = "http://localhost:5103", IsEnabled = false,
                TimeoutSeconds = 10, MaxResponseBytes = 1_048_576, CreatedAt = SeedTime, UpdatedAt = SeedTime
            });

        modelBuilder.Entity<EncryptionProfile>().HasData(
            new EncryptionProfile
            {
                Id = 1, Name = "payments-aes", Algorithm = "AES-256-GCM", KeyReference = "payments-v1",
                CreatedAt = SeedTime, UpdatedAt = SeedTime
            });

        modelBuilder.Entity<Operation>().HasData(
            // Basic case: plain request, parameter in the path.
            new Operation
            {
                Id = 1, Key = "customer.get", UrlId = "crm-customers", DestinationId = 1,
                HttpMethod = "GET", PathTemplate = "/customers/{customerId}", IsEnabled = true,
                MaxRequestBytes = 4096, CreatedAt = SeedTime, UpdatedAt = SeedTime
            },
            // Query parameters, and an operation that allows unknown parameters.
            new Operation
            {
                Id = 2, Key = "customer.search", UrlId = "crm-customer-search", DestinationId = 1,
                HttpMethod = "GET", PathTemplate = "/customers", IsEnabled = true,
                MaxRequestBytes = 4096, AllowUnknownParameters = true, CreatedAt = SeedTime, UpdatedAt = SeedTime
            },
            // POST with a body, encryption required.
            new Operation
            {
                Id = 3, Key = "payment.create", UrlId = "payments", DestinationId = 2,
                HttpMethod = "POST", PathTemplate = "/payments", IsEnabled = true,
                MaxRequestBytes = 8192, RequiresEncryption = true, EncryptionProfileId = 1,
                CreatedAt = SeedTime, UpdatedAt = SeedTime
            },
            // Disabled operation.
            new Operation
            {
                Id = 4, Key = "customer.export", UrlId = "crm-customer-export", DestinationId = 1,
                HttpMethod = "GET", PathTemplate = "/customers/export", IsEnabled = false,
                MaxRequestBytes = 4096, CreatedAt = SeedTime, UpdatedAt = SeedTime
            },
            // Enabled operation on a disabled destination.
            new Operation
            {
                Id = 5, Key = "invoice.get", UrlId = "billing-invoices", DestinationId = 3,
                HttpMethod = "GET", PathTemplate = "/invoices/{invoiceId}", IsEnabled = true,
                MaxRequestBytes = 4096, CreatedAt = SeedTime, UpdatedAt = SeedTime
            });

        modelBuilder.Entity<ParameterRule>().HasData(
            new ParameterRule { Id = 1, OperationId = 1, Name = "customerId", Location = ParameterLocation.Path,
                DataType = ParameterDataType.Integer, IsRequired = true, MinValue = 1 },

            new ParameterRule { Id = 2, OperationId = 2, Name = "name", Location = ParameterLocation.Query,
                DataType = ParameterDataType.String, IsRequired = true, MaxLength = 100 },
            new ParameterRule { Id = 3, OperationId = 2, Name = "page", Location = ParameterLocation.Query,
                DataType = ParameterDataType.Integer, IsRequired = false, MinValue = 1, MaxValue = 1000 },

            new ParameterRule { Id = 4, OperationId = 3, Name = "accountId", Location = ParameterLocation.Body,
                DataType = ParameterDataType.Integer, IsRequired = true, MinValue = 1 },
            new ParameterRule { Id = 5, OperationId = 3, Name = "amount", Location = ParameterLocation.Body,
                DataType = ParameterDataType.Decimal, IsRequired = true, MinValue = 0.01m, MaxValue = 100_000m },
            new ParameterRule { Id = 6, OperationId = 3, Name = "currency", Location = ParameterLocation.Body,
                DataType = ParameterDataType.String, IsRequired = true, MaxLength = 3 },

            new ParameterRule { Id = 7, OperationId = 5, Name = "invoiceId", Location = ParameterLocation.Path,
                DataType = ParameterDataType.Integer, IsRequired = true, MinValue = 1 });

        modelBuilder.Entity<HeaderRule>().HasData(
            new HeaderRule { Id = 1, OperationId = 1, HeaderName = "Accept-Language" },
            new HeaderRule { Id = 2, OperationId = 2, HeaderName = "Accept-Language" },
            // Lets the caller pass an idempotency key so the upstream can detect a duplicate POST.
            new HeaderRule { Id = 3, OperationId = 3, HeaderName = "Idempotency-Key" });

        modelBuilder.Entity<OperationalSetting>().HasData(
            new OperationalSetting
            {
                Name = OperationalSettingNames.GlobalMaxRequestBytes, Value = "65536",
                Description = "Largest request accepted at all, checked before the operation is known."
            },
            new OperationalSetting
            {
                Name = OperationalSettingNames.ConfigurationCacheSeconds, Value = "30",
                Description = "How many seconds operation configuration is kept in the memory cache."
            });
    }
}
