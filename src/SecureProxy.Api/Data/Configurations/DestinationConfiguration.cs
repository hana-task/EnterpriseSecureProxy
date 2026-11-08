using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SecureProxy.Api.Data.Entities;

namespace SecureProxy.Api.Data.Configurations;

public class DestinationConfiguration : IEntityTypeConfiguration<Destination>
{
    public void Configure(EntityTypeBuilder<Destination> destination)
    {
        destination.ToTable("Destinations", table =>
        {
            table.IsTemporal();
            table.HasCheckConstraint("CK_Destinations_TimeoutSeconds", "[TimeoutSeconds] BETWEEN 1 AND 300");
            table.HasCheckConstraint("CK_Destinations_MaxResponseBytes", "[MaxResponseBytes] > 0");
        });

        destination.Property(d => d.Name).HasMaxLength(100);
        destination.HasIndex(d => d.Name).IsUnique();
        destination.Property(d => d.BaseUrl).HasMaxLength(500);
        destination.Property(d => d.RowVersion).IsRowVersion();
    }
}
