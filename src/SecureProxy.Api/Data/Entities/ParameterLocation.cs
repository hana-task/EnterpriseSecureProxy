namespace SecureProxy.Api.Data.Entities;

/// <summary>Where a validated parameter is placed in the upstream request.</summary>
public enum ParameterLocation
{
    Path,
    Query,
    Body
}
