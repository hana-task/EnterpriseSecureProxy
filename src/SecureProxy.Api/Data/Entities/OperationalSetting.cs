namespace SecureProxy.Api.Data.Entities;

/// <summary>
/// A proxy-wide setting that does not belong to a single operation (assignment 3, "operational settings").
/// A simple name/value table.
/// </summary>
public class OperationalSetting
{
    /// <summary>Setting name, also the primary key. See <see cref="OperationalSettingNames"/>.</summary>
    public string Name { get; set; } = null!;

    public string Value { get; set; } = null!;

    public string Description { get; set; } = null!;
}
