using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SecureProxy.Api.Data.Entities;

namespace SecureProxy.Api.Data.Configurations;

public class OperationConfiguration : IEntityTypeConfiguration<Operation>
{
    public void Configure(EntityTypeBuilder<Operation> operation)
    {
        operation.ToTable("Operations", table =>
        {
            table.IsTemporal();
            table.HasCheckConstraint("CK_Operations_HttpMethod",
                "[HttpMethod] IN ('GET', 'POST', 'PUT', 'PATCH', 'DELETE')");
            table.HasCheckConstraint("CK_Operations_EncryptionNeedsProfile",
                "[RequiresEncryption] = 0 OR [EncryptionProfileId] IS NOT NULL");
            table.HasCheckConstraint("CK_Operations_MaxRequestBytes", "[MaxRequestBytes] > 0");
        });

        operation.Property(o => o.Key).HasMaxLength(100);
        operation.HasIndex(o => o.Key).IsUnique();
        operation.Property(o => o.UrlId).HasMaxLength(100);
        operation.Property(o => o.HttpMethod).HasMaxLength(10);
        operation.Property(o => o.PathTemplate).HasMaxLength(500);
        operation.Property(o => o.RowVersion).IsRowVersion();

        // A destination that still has operations cannot be deleted (disable it instead).
        operation.HasOne(o => o.Destination)
            .WithMany(d => d.Operations)
            .HasForeignKey(o => o.DestinationId)
            .OnDelete(DeleteBehavior.Restrict);

        operation.HasOne(o => o.EncryptionProfile)
            .WithMany()
            .HasForeignKey(o => o.EncryptionProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
