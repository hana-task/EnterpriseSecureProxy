using System.Text;
using System.Text.Json;
using SecureProxy.Api.Data.Entities;

namespace SecureProxy.Api.Validation;

/// <summary>
/// Builds the JSON body for the destination from scratch, out of the validated body parameters only.
/// The caller's own body is never copied.
/// </summary>
public static class UpstreamBodyBuilder
{
    public static string Build(IEnumerable<ValidatedParameter> bodyParameters)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var parameter in bodyParameters)
            {
                if (parameter.DataType == ParameterDataType.String)
                {
                    // The writer escapes quotes and control characters, so text cannot break the JSON.
                    writer.WriteString(parameter.Name, parameter.CanonicalText);
                }
                else
                {
                    // Numbers and booleans were parsed and re-formatted by ParameterCheck, so the text is safe.
                    writer.WritePropertyName(parameter.Name);
                    writer.WriteRawValue(parameter.CanonicalText);
                }
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
