using SecureProxy.Api.Data.Entities;

namespace SecureProxy.Api.Validation;

/// <summary>
/// A parameter that passed its rule. CanonicalText is the value in one standard form
/// ("007" becomes "7", true becomes "true"), so later steps place exactly what was validated.
/// </summary>
public sealed record ValidatedParameter(
    string Name, ParameterLocation Location, ParameterDataType DataType, string CanonicalText);
