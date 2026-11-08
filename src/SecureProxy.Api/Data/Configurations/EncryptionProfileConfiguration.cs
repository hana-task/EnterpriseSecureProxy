using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SecureProxy.Api.Data.Entities;

namespace SecureProxy.Api.Data.Configurations;

public class EncryptionProfileConfiguration : IEntityTypeConfiguration<EncryptionProfile>
{
    public void Configure(EntityTypeBuilder<EncryptionProfile> profile)
    {
        profile.ToTable("EncryptionProfiles", table => table.IsTemporal());

        profile.Property(p => p.Name).HasMaxLength(100);
        profile.HasIndex(p => p.Name).IsUnique();
        profile.Property(p => p.Algorithm).HasMaxLength(50);
        profile.Property(p => p.KeyReference).HasMaxLength(100);
    }
}
