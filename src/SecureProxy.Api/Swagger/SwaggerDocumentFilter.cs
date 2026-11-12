using Microsoft.OpenApi.Models;
using SecureProxy.Api.Admin;
using SecureProxy.Api.Controllers;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace SecureProxy.Api.Swagger;

/// <summary>
/// Adds to the Swagger document what the controllers do not declare, so the API can be tried by hand:
/// - the proxy endpoint reads the raw body itself (no [FromBody]), so Swagger would show no body box.
///   Here it gets a JSON body with ready-made sample requests against the seed data;
/// - the admin endpoints need the X-Admin-Key header, entered once with the Authorize button.
/// Documentation only: it changes nothing in how requests are handled.
/// </summary>
public sealed class SwaggerDocumentFilter : IOperationFilter
{
    public const string AdminKeySchemeName = "AdminKey";

    /// <summary>Sample requests: name shown in the Swagger list → request body.</summary>
    private static readonly (string Name, string Json)[] ProxyExamples =
    [
        ("Valid request", """{ "clientId": "customer.get", "json": { "urlId": "crm-customers", "customerId": "123" } }"""),
        ("Unknown parameters, allowed and not forwarded", """{ "clientId": "customer.search", "json": { "urlId": "crm-customer-search", "name": "cohen", "page": 2, "debug": true } }"""),
        ("Unknown operation (404)", """{ "clientId": "no.such", "json": { "urlId": "x" } }"""),
        ("Disabled operation (403)", """{ "clientId": "customer.export", "json": { "urlId": "crm-customer-export" } }"""),
        ("Disabled destination (503)", """{ "clientId": "invoice.get", "json": { "urlId": "billing-invoices", "invoiceId": "5" } }"""),
        ("Caller tries to supply a URL (400)", """{ "clientId": "customer.get", "json": { "urlId": "crm-customers", "customerId": "123", "url": "http://evil" } }"""),
        ("Wrong urlId (400)", """{ "clientId": "customer.get", "json": { "urlId": "http://evil/customers", "customerId": "123" } }"""),
        ("Missing required parameter (400)", """{ "clientId": "customer.get", "json": { "urlId": "crm-customers" } }"""),
        ("Path traversal attempt (400)", """{ "clientId": "customer.get", "json": { "urlId": "crm-customers", "customerId": "../admin" } }"""),
        ("Plain parameters for an encrypted operation (400)", """{ "clientId": "payment.create", "json": { "urlId": "payments", "amount": 10 } }""")
    ];

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var controller = context.MethodInfo.DeclaringType;

        if (controller == typeof(ProxyController))
            AddProxyRequestBody(operation);

        if (controller == typeof(AdminOperationsController))
            RequireAdminKey(operation);
    }

    private static void AddProxyRequestBody(OpenApiOperation operation)
    {
        var jsonContent = new OpenApiMediaType { Schema = new OpenApiSchema { Type = "object" } };
        foreach (var (name, json) in ProxyExamples)
            jsonContent.Examples[name] = new OpenApiExample { Value = OpenApiAnyFactory.CreateFromJson(json) };

        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content = { ["application/json"] = jsonContent }
        };
    }

    private static void RequireAdminKey(OpenApiOperation operation)
    {
        var scheme = new OpenApiSecurityScheme
        {
            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = AdminKeySchemeName }
        };
        operation.Security.Add(new OpenApiSecurityRequirement { [scheme] = [] });
    }

    /// <summary>The Authorize button: the admin key is typed once and sent as the X-Admin-Key header.</summary>
    public static OpenApiSecurityScheme AdminKeyScheme() => new()
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = AdminKeyFilter.HeaderName,
        Description = "The value of Admin:ApiKey (user-secrets)."
    };
}
