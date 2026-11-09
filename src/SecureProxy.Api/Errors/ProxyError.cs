namespace SecureProxy.Api.Errors;

/// <summary>
/// An error the proxy itself returns (never an answer from the destination).
/// ErrorCode is fixed and documented, so callers can rely on it instead of parsing the message.
/// Detail must be safe to show: no caller values, hostnames, configuration or encryption details.
/// </summary>
public sealed record ProxyError(int StatusCode, string ErrorCode, string Title, string Detail);
