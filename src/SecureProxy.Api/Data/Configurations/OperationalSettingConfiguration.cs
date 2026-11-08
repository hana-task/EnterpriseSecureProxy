using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SecureProxy.Api.Data.Entities;

namespace SecureProxy.Api.Data.Configurations;

public class OperationalSettingConfiguration : IEntityTypeConfiguration<OperationalSetting>
{
    public void Configure(EntityTypeBuilder<OperationalSetting> setting)
    {
        setting.ToTable("OperationalSettings", table => table.IsTemporal());

        setting.HasKey(s => s.Name);
        setting.Property(s => s.Name).HasMaxLength(100);
        setting.Property(s => s.Value).HasMaxLength(500);
        setting.Property(s => s.Description).HasMaxLength(500);
    }
}
