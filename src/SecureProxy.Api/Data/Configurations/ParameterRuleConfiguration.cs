using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SecureProxy.Api.Data.Entities;

namespace SecureProxy.Api.Data.Configurations;

public class ParameterRuleConfiguration : IEntityTypeConfiguration<ParameterRule>
{
    public void Configure(EntityTypeBuilder<ParameterRule> rule)
    {
        rule.ToTable("ParameterRules", table => table.IsTemporal());

        rule.Property(r => r.Name).HasMaxLength(100);
        rule.HasIndex(r => new { r.OperationId, r.Name }).IsUnique();

        // Enums are stored as text ("Path", "Integer") so the table is readable in SQL.
        rule.Property(r => r.Location).HasConversion<string>().HasMaxLength(10);
        rule.Property(r => r.DataType).HasConversion<string>().HasMaxLength(10);
        rule.Property(r => r.MinValue).HasPrecision(18, 4);
        rule.Property(r => r.MaxValue).HasPrecision(18, 4);

        // Rules belong to their operation and are deleted with it.
        rule.HasOne(r => r.Operation)
            .WithMany(o => o.ParameterRules)
            .HasForeignKey(r => r.OperationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
