using System.Text.Json;

namespace SecureProxy.Api.Validation;

/// <summary>
/// The outer shape of a valid request: the operation key, and the "json" object
/// (urlId plus parameters, or urlId plus the encrypted fields). Contents are checked by later steps.
/// BodySizeBytes is kept so the operation's own size limit can be checked once the operation is known.
/// </summary>
public sealed record RequestEnvelope(string ClientId, JsonElement Json, int BodySizeBytes);
