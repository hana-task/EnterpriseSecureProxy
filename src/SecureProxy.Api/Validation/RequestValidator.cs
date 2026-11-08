using SecureProxy.Api.Policies;
using SecureProxy.Api.Security.Encryption;
using SecureProxy.Api.Security.Headers;

namespace SecureProxy.Api.Validation;

/// <summary>
/// Runs every check before forwarding, in a fixed order, and stops at the first failure.
/// Returns either a ready-to-send UpstreamRequest or the Rejection. Nothing is sent from here.
///
/// The order matters: cheap checks on the request itself come first; the configuration is read
/// only once the request is known to be well-formed; decryption comes before parameter
/// validation because the parameters are inside the encrypted payload.
/// </summary>
public sealed class RequestValidator
{
    private readonly IPolicyProvider _policyProvider;
    private readonly AesGcmPayloadDecryptor _decryptor;

    public RequestValidator(IPolicyProvider policyProvider, AesGcmPayloadDecryptor decryptor)
    {
        _policyProvider = policyProvider;
        _decryptor = decryptor;
    }

    public async Task<PreForwardResult> ValidateAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        // 1. The shape of the request. The operation is not known yet.
        var settings = await _policyProvider.GetSettingsAsync(cancellationToken);
        var envelope = await EnvelopeCheck.CheckAsync(request, settings.GlobalMaxRequestBytes, cancellationToken);
        if (!envelope.Passed)
            return Rejected(envelope.Rejection!, operationKey: null, operation: null);

        // From here on we know which operation was asked for, and all checks use its policy.
        var operationKey = envelope.Value!.ClientId;
        var foundPolicy = await _policyProvider.FindOperationAsync(operationKey, cancellationToken);

        // 2. The operation exists, is enabled, its destination is enabled, and the size fits.
        var operation = OperationCheck.Check(foundPolicy, envelope.Value);
        if (!operation.Passed)
            return Rejected(operation.Rejection!, operationKey, foundPolicy);
        var policy = operation.Value!;

        // 3. urlId matches the configured one.
        var fields = UrlIdCheck.Check(envelope.Value, policy);
        if (!fields.Passed)
            return Rejected(fields.Rejection!, operationKey, policy);

        // 4. Decrypt if the operation requires it.
        var parameterFields = DecryptionCheck.Check(fields.Value!, policy, _decryptor);
        if (!parameterFields.Passed)
            return Rejected(parameterFields.Rejection!, operationKey, policy);

        // 5. Parameters follow the rules.
        var parameters = ParameterCheck.Check(parameterFields.Value!, policy);
        if (!parameters.Passed)
            return Rejected(parameters.Rejection!, operationKey, policy);

        // 6. Build the request: method, address and body, from configuration and validated parameters.
        var upstreamRequest = UpstreamRequestBuilder.Build(parameters.Value!, policy);
        if (!upstreamRequest.Passed)
            return Rejected(upstreamRequest.Rejection!, operationKey, policy);

        // 7. Choose the headers to forward. This never rejects.
        var headers = HeaderFilter.Filter(request.Headers, policy.AllowedHeaders);

        return new PreForwardResult
        {
            OperationKey = operationKey,
            Operation = policy,
            Request = upstreamRequest.Value! with { Headers = headers }
        };
    }

    private static PreForwardResult Rejected(Rejection rejection, string? operationKey, OperationPolicy? operation) =>
        new() { OperationKey = operationKey, Operation = operation, Rejection = rejection };
}
