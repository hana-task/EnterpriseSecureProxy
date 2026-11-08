using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SecureProxy.Api.Data.Entities;

namespace SecureProxy.Api.Data.Configurations;

public class HeaderRuleConfiguration : IEntityTypeConfiguration<HeaderRule>
{
    public void Configure(EntityTypeBuilder<HeaderRule> rule)
    {
        rule.ToTable("HeaderRules", table => table.IsTemporal());

        rule.Property(r => r.HeaderName).HasMaxLength(100);
        rule.HasIndex(r => new { r.OperationId, r.HeaderName }).IsUnique();

        rule.HasOne(r => r.Operation)
            .WithMany(o => o.HeaderRules)
            .HasForeignKey(r => r.OperationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
