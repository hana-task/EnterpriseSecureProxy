using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SecureProxy.Api.Data.Entities;

namespace SecureProxy.Api.Data.Configurations;

public class RequestLogConfiguration : IEntityTypeConfiguration<RequestLog>
{
    public void Configure(EntityTypeBuilder<RequestLog> log)
    {
        // Not temporal: a row is saved before forwarding, completed once at the end, and never changed after that.
        log.ToTable("RequestLogs");

        log.Property(l => l.CorrelationId).HasMaxLength(64);
        log.Property(l => l.OperationKey).HasMaxLength(100);
        log.Property(l => l.DestinationName).HasMaxLength(100);
        log.Property(l => l.Outcome).HasConversion<string>().HasMaxLength(30);
        log.Property(l => l.Reason).HasMaxLength(100);

        // The two ways an investigation searches: by request id, and by time range.
        log.HasIndex(l => l.CorrelationId);
        log.HasIndex(l => l.StartedAt);
    }
}
