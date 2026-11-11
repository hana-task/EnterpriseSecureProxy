using Microsoft.EntityFrameworkCore;
using SecureProxy.Api.Data;
using SecureProxy.Api.Data.Entities;
using SecureProxy.Api.Policies;

namespace SecureProxy.Api.Admin;

/// <summary>
/// Reads and updates an operation's settings with optimistic concurrency (assignment section 6, Concurrency).
///
/// The caller sends back the rowversion it read. EF Core puts it in the WHERE clause of the UPDATE, so the
/// check and the write are one SQL statement: if anyone changed the row in between, no row matches,
/// nothing is written, and the update is reported as a conflict (409). SQL Server sets the new rowversion itself.
///
/// History: Operations is a temporal table, so SQL Server keeps the previous version of the row (assignment 5.7).
/// </summary>
public sealed class OperationAdminService
{
    private readonly ProxyDbContext _db;
    private readonly IPolicyProvider _policyProvider;
    private readonly ILogger<OperationAdminService> _logger;

    public OperationAdminService(ProxyDbContext db, IPolicyProvider policyProvider, ILogger<OperationAdminService> logger)
    {
        _db = db;
        _policyProvider = policyProvider;
        _logger = logger;
    }

    public async Task<OperationSettings?> GetAsync(string operationKey, CancellationToken cancellationToken)
    {
        var operation = await FindAsync(_db.Operations.AsNoTracking(), operationKey, cancellationToken);
        return operation is null ? null : ToSettings(operation);
    }

    /// <summary>Call only with a request whose FindProblem() returned null.</summary>
    public async Task<OperationUpdateResult> UpdateAsync(
        string operationKey, OperationUpdateRequest request, CancellationToken cancellationToken)
    {
        var operation = await FindAsync(_db.Operations, operationKey, cancellationToken);
        if (operation is null)
            return new OperationUpdateResult(OperationUpdateStatus.NotFound);

        // The heart of optimistic concurrency: tell EF that the version we "started from" is the caller's,
        // not the one just loaded. EF then writes: UPDATE ... WHERE Id = @id AND RowVersion = @callerVersion.
        _db.Entry(operation).Property(o => o.RowVersion).OriginalValue = request.RowVersion!;

        operation.IsEnabled = request.IsEnabled!.Value;
        operation.MaxRequestBytes = request.MaxRequestBytes!.Value;
        // Always changes, so EF always sends the UPDATE (and with it the version check). Without a changed
        // column EF would send nothing, and a stale request with unchanged values would pass as a success.
        operation.UpdatedAt = DateTime.UtcNow;

        try
        {
            // Not the caller's token: a caller who disconnects during the save could otherwise leave the change
            // committed in SQL Server but the cache not cleared (old values used for up to ConfigurationCacheSeconds).
            // Once the write starts, it finishes and the cache is cleared (the same rule as the audit saves).
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogWarning("Update of operation {OperationKey} rejected: stale rowversion", operationKey);
            return new OperationUpdateResult(OperationUpdateStatus.Conflict);
        }

        // Only after the database accepted the change: the next request on this instance reads the new values.
        // Requests that already hold the old policy finish with it (see DESIGN-NOTES, in-flight requests).
        _policyProvider.InvalidateAll();

        _logger.LogInformation("Operation {OperationKey} updated: IsEnabled={IsEnabled}, MaxRequestBytes={MaxRequestBytes}",
            operationKey, operation.IsEnabled, operation.MaxRequestBytes);
        return new OperationUpdateResult(OperationUpdateStatus.Updated, ToSettings(operation));
    }

    /// <summary>
    /// SQL Server compares text case-insensitively, so the key is checked again in code:
    /// operation keys must match exactly (as in PolicyRepository).
    /// </summary>
    private static async Task<Operation?> FindAsync(
        IQueryable<Operation> operations, string operationKey, CancellationToken cancellationToken)
    {
        var operation = await operations.FirstOrDefaultAsync(o => o.Key == operationKey, cancellationToken);
        return operation is not null && string.Equals(operation.Key, operationKey, StringComparison.Ordinal)
            ? operation
            : null;
    }

    private static OperationSettings ToSettings(Operation operation) =>
        new(operation.Key, operation.IsEnabled, operation.MaxRequestBytes, operation.RowVersion);
}
