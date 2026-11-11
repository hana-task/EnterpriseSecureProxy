namespace SecureProxy.Api.Admin;

public enum OperationUpdateStatus
{
    Updated,

    /// <summary>No operation with this key.</summary>
    NotFound,

    /// <summary>The row changed (or was deleted) since the caller read it: the sent rowversion is old.</summary>
    Conflict
}

/// <summary>The result of an update. Settings holds the new values and the new version when Status is Updated.</summary>
public sealed record OperationUpdateResult(OperationUpdateStatus Status, OperationSettings? Settings = null);
