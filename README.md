# Enterprise Secure Proxy

A secure, configuration-driven, fail-closed proxy built with ASP.NET Core 8, EF Core and SQL Server.

The caller names a configured operation, and the proxy decides everything else from the database: destination,
HTTP method, path, allowed parameters and headers, and whether the payload must be encrypted. If any check
fails, the request never reaches the destination.

The solution was built from scratch.

| Document | Content |
|---|---|
| [DESIGN-NOTES.md](DESIGN-NOTES.md) | Architecture, interpretation of the requirements, and the technical decisions |
| [DEFECT-REPORT.md](DEFECT-REPORT.md) | Real defects found and fixed during development |
| [AI-USAGE.md](AI-USAGE.md) | AI usage declaration |

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), version 8.0.427 or a later 8.0.4xx patch
  (pinned in `global.json`).
- [Docker](https://www.docker.com/) for SQL Server 2022. An existing SQL Server instance works too; use its
  connection string in step 2.

## Setup

### 1. Start SQL Server and the demo destination

From the repository root:

```bash
docker compose up -d
```

[docker-compose.yml](docker-compose.yml) starts two containers:

- **SQL Server 2022** on port 14330 (avoids a clash with a local SQL Server on 1433). The password in the file is
  an example for this local container only; any strong password works if the same value is used in step 2.
  SQL Server needs about 10–20 seconds before it accepts connections.
- **An echo server** on port 5101, standing in for the `crm` destination of the sample data. It answers with
  what it received, so a demo request shows exactly what the proxy forwarded. The automated tests do not need it.

`docker compose down` stops them; the database is kept in a volume (`down -v` deletes it).

### 2. Configure the secrets

Secrets are never in `appsettings.json`. In development they are kept in
[user-secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets), outside the repository.
Run from the repository root:

```bash
dotnet user-secrets set "ConnectionStrings:ProxyDb" "Server=127.0.0.1,14330;Database=SecureProxy;User Id=sa;Password=Proxy#Dev2026!;TrustServerCertificate=True" --project src/SecureProxy.Api

dotnet user-secrets set "Admin:ApiKey" "choose-a-long-random-value" --project src/SecureProxy.Api
```

- Use `127.0.0.1`, not `localhost`: the container listens on IPv4 only, and `localhost` may resolve to IPv6 first.
- The tests read the same connection string, but use a separate database (`SecureProxy_Tests`), so the
  development data is never touched.
- Optional: the key of the encrypted sample operation `payment.create` (base64 of 32 random bytes).
  Without it, that operation is rejected (fail closed). The tests use their own fixed key.

  ```bash
  dotnet user-secrets set "ProxyKeys:payments-v1" "<base64 of 32 bytes, e.g. from: openssl rand -base64 32>" --project src/SecureProxy.Api
  ```

Outside development, the same values come from environment variables:
`ConnectionStrings__ProxyDb`, `Admin__ApiKey`, `ProxyKeys__payments-v1`.

### 3. Run

```bash
dotnet run --project src/SecureProxy.Api
```

Or open `EnterpriseSecureProxy.sln` in Visual Studio and press F5.

- In the Development environment, the app creates or updates the database on startup (EF Core migration with the
  sample data). In production, migrations would run as a separate deployment step (see DESIGN-NOTES).
- Swagger UI: http://localhost:5157/swagger (the `https` profile: https://localhost:7263/swagger).
- Logs: the console, and a daily JSON file in `src/SecureProxy.Api/logs/`.

## Using the proxy

One endpoint: `POST /api/proxy/execute`, with a JSON body.

```json
{ "clientId": "customer.get", "json": { "urlId": "crm-customers", "customerId": "123" } }
```

- `clientId`: the key of a configured operation.
- `json.urlId`: the id of the operation's configured endpoint. It is never an address.
- The other fields of `json`: the operation's parameters, checked against its rules.
- For an operation that requires encryption, the parameters arrive AES-256-GCM encrypted:
  `{ "urlId": "...", "nonce": "...", "ciphertext": "...", "tag": "..." }` (base64).

The caller never sends a URL, an HTTP method or a path. A `url` or `method` field is an unknown parameter and is
rejected.

### Interpretation of the request contract

The assignment's example shows `"json": ???` and states only that `json` must contain the `urlId` key. We
interpret `urlId` as the **name of a configured endpoint**, not an address: instead of sending a URL, the caller
sends the name of one that only the proxy knows. Each operation has exactly one `urlId` in configuration, and the
request is rejected unless the value matches exactly. The real address is built only from configuration: the
destination's base URL plus the operation's path template, filled with the validated parameters. The full
reasoning is in DESIGN-NOTES ("Interpretation of unclear requirements").

### Sample configuration (seed data)

| Operation | Method and path | Notes |
|---|---|---|
| `customer.get` | `GET /customers/{customerId}` | `customerId` is a required integer |
| `customer.search` | `GET /customers?name=&page=` | Unknown parameters allowed (accepted, never forwarded) |
| `payment.create` | `POST /payments` | Requires encryption, body parameters |
| `customer.export` | `GET /customers/export` | Disabled operation |
| `invoice.get` | `GET /invoices/{invoiceId}` | Its destination is disabled |

The `crm` destination (`http://localhost:5101`) is the echo server from setup step 1, so the `customer.*`
operations can be tried end to end. Nothing runs on the `payments` destination (`5102`), so `payment.create`
ends with `502 upstream_unreachable` after passing all the checks; it is covered by the automated tests.

### Example responses

Every response carries `X-Correlation-ID` (the caller's own value is kept if it is valid). The same id is in the
audit table `RequestLogs` and in the technical log. `X-Proxy-Source` tells who produced the response: `upstream`
for the destination's answer, passed on with its own status code, and `proxy` for an error generated by the
proxy, always in the same format.

A valid request, with headers the destination must not receive:

```http
POST /api/proxy/execute
Accept-Language: he
Cookie: session=secret
Authorization: Bearer abc
X-Custom: 1
{ "clientId": "customer.get", "json": { "urlId": "crm-customers", "customerId": "123" } }

HTTP/1.1 200 OK
X-Proxy-Source: upstream
{ "path": "/customers/123", "method": "GET", "headers": { "host": "localhost:5101", "accept-language": "he" }, ... }
```

The echo shows what the destination received: the path built from the template, and only `Accept-Language`,
the one header on this operation's allowlist. `Cookie` and `Authorization` are on the fixed block list, and
`X-Custom` is not allowed.

Unknown parameters, on an operation that allows them:

```http
POST /api/proxy/execute
{ "clientId": "customer.search", "json": { "urlId": "crm-customer-search", "name": "cohen", "page": 2, "debug": true } }

HTTP/1.1 200 OK
X-Proxy-Source: upstream
{ "path": "/customers", "query": { "name": "cohen", "page": "2" }, ... }
```

`debug` is accepted but not forwarded: only validated parameters reach the destination.

Unknown operation:

```http
POST /api/proxy/execute
{ "clientId": "no.such", "json": { "urlId": "x" } }

HTTP/1.1 404 Not Found
X-Proxy-Source: proxy
{"title":"Unknown operation","status":404,"detail":"The requested operation does not exist.","errorCode":"unknown_operation","correlationId":"6e79b5a0..."}
```

The caller tries to supply a URL:

```http
POST /api/proxy/execute
{ "clientId": "customer.get", "json": { "urlId": "crm-customers", "customerId": "123", "url": "http://evil" } }

HTTP/1.1 400 Bad Request
{"title":"Unknown parameter","status":400,"detail":"The request contains a parameter this operation does not accept.","errorCode":"unknown_parameter",...}
```

Operation on a disabled destination:

```http
POST /api/proxy/execute
{ "clientId": "invoice.get", "json": { "urlId": "billing-invoices", "invoiceId": "5" } }

HTTP/1.1 503 Service Unavailable
{"title":"Destination unavailable","status":503,"detail":"The destination of this operation is disabled.","errorCode":"destination_unavailable",...}
```

The full list of error codes and their status codes is in DESIGN-NOTES ("Endpoint and error responses").

### Admin API: updating an operation

`GET` and `PUT /api/admin/operations/{key}`, with the header `X-Admin-Key` (the value of `Admin:ApiKey`).
Only `isEnabled` and `maxRequestBytes` can be changed. The update must send the `rowVersion` it read; if someone
changed the operation in between, the update is rejected with 409 and nothing is saved.

```http
GET /api/admin/operations/customer.search
X-Admin-Key: <your admin key>

HTTP/1.1 200 OK
{"key":"customer.search","isEnabled":true,"maxRequestBytes":4096,"rowVersion":"AAAAAAAAB9U="}
```

```http
PUT /api/admin/operations/customer.search
X-Admin-Key: <your admin key>
{ "isEnabled": false, "maxRequestBytes": 4096, "rowVersion": "AAAAAAAAB9U=" }

HTTP/1.1 200 OK            (the response carries the new rowVersion)
```

Sending the same `PUT` again, with the old `rowVersion`:

```http
HTTP/1.1 409 Conflict
{"title":"Concurrency conflict","status":409,"detail":"The configuration was changed by someone else. Read it again and retry the update.","errorCode":"concurrency_conflict",...}
```

An update through the API takes effect at once (the configuration cache is cleared). A change made directly in
SQL takes effect within the cache lifetime (30 seconds).

## Tests

SQL Server must be running and the connection string configured (setup steps 1–2). Then:

```bash
dotnet test
```

- xUnit unit tests for each component, and integration tests that start the real application in memory
  (`WebApplicationFactory`) against the `SecureProxy_Tests` database, rebuilt on every run.
- The destination is a recording fake (`RecordingHandler`) that records every request it receives. Every
  rejection test asserts that the destination received nothing; a status code alone is not taken as proof.

### Required scenarios (assignment section 9)

The integration tests are in `tests/SecureProxy.Tests/Integration/`: `ProxyEndpointTests.cs`, and its parts
`ProxyEndpointTests.Admin.cs` and `ProxyEndpointTests.RateLimiting.cs`.

| # | Scenario | Tests |
|---|---|---|
| 1 | Valid operation is forwarded | `Valid_operation_is_forwarded_and_the_destination_answer_is_returned`, `Valid_encrypted_request_is_decrypted_and_forwarded` |
| 2 | Unknown operation is rejected | `Unknown_operation_is_rejected` |
| 3 | Disabled operation is rejected | `Disabled_operation_is_rejected` |
| 4 | Disabled destination is rejected | `Operation_on_a_disabled_destination_is_rejected` |
| 5 | Caller-supplied URL cannot override configuration | `Url_field_from_the_caller_is_rejected`, `Raw_url_in_place_of_the_url_id_is_rejected`, `Url_field_is_ignored_by_an_operation_that_allows_unknown_parameters` |
| 6 | Invalid or disallowed method is rejected | `Method_field_from_the_caller_is_rejected`, `Endpoint_accepts_only_post`, `Operation_with_a_disallowed_method_cannot_be_configured` |
| 7 | Missing required parameter is rejected | `Missing_required_parameter_is_rejected` |
| 8 | Unknown parameters are handled according to policy | `Unknown_parameter_is_rejected_when_the_operation_does_not_allow_it`, `Unknown_parameter_is_accepted_but_not_forwarded_when_the_operation_allows_it` |
| 9 | Path traversal is rejected | `Path_traversal_in_a_path_parameter_is_rejected` |
| 10 | Disallowed headers are not forwarded | `Only_allowed_headers_reach_the_destination`, `Real_network_handler_adds_no_tracing_headers_and_follows_no_redirects` |
| 11 | Invalid AES-GCM authentication tag is rejected | `Invalid_authentication_tag_is_rejected` (and unit tests in `Security/`) |
| 12 | Invalid encrypted payload is never forwarded | `Payload_that_is_not_valid_base64_is_never_forwarded`, `Payload_encrypted_for_another_operation_is_never_forwarded`, `Plain_parameters_for_an_operation_that_requires_encryption_are_never_forwarded` |
| 13 | Upstream timeout is handled | `Destination_that_does_not_answer_in_time_gets_a_504_from_the_proxy` |
| 14 | Upstream HTTP error is handled | `Destination_error_status_is_passed_on_as_is_and_marked_as_upstream` |
| 15 | Cancellation is propagated | `Caller_who_cancels_cancels_the_request_to_the_destination` |
| 16 | Configuration concurrency conflict is handled | `Update_based_on_an_old_version_is_rejected_with_409_and_changes_nothing`, `Stale_update_with_unchanged_values_is_still_rejected` |
| 17 | Audit records for successful and rejected requests | `Successful_request_is_audited_as_forwarded_with_both_status_codes`, `Rejected_request_is_audited_as_not_forwarded_with_the_reason`, `Request_is_not_forwarded_when_its_audit_record_cannot_be_saved` |

## Project structure

```
src/SecureProxy.Api/
  Controllers/   ProxyController (POST /api/proxy/execute), AdminOperationsController
  Proxy/         ProxyService: the flow of one request
  Validation/    The checks before forwarding, run in order by RequestValidator
  Security/      AES-GCM decryption, key source, header allowlist and fixed block list
  Forwarding/    URL and body builder, forwarding with timeout and streaming, response writer
  Policies/      The operation's configuration as an immutable record, with the cache
  Data/          DbContext, entities, EF configuration, seed data, migrations, repository
  Audit/         Correlation id middleware and the RequestLogs audit recorder
  Errors/        Error catalogue (one place for status codes) and the unexpected-exception handler
  Admin/         Admin key check and the operation update service
  Resilience/    Rate limiting and the concurrency limit
tests/SecureProxy.Tests/   Unit tests per folder, and Integration/
```

## Configuration

| Setting | Where | Default |
|---|---|---|
| Connection string | `ConnectionStrings:ProxyDb` (user-secrets / env) | – (the app does not start without it) |
| Admin key | `Admin:ApiKey` (user-secrets / env) | – (the admin API rejects every request without it) |
| Encryption keys | `ProxyKeys:{keyReference}` (user-secrets / env) | – (the operation is rejected without it) |
| Rate limits | `RateLimiting` in `appsettings.json` | 100 proxy and 10 admin requests per minute per IP, 100 concurrent requests |
| Global request size limit, cache lifetime | `OperationalSettings` table | 64 KB, 30 seconds |
| Destinations, operations, parameter and header rules | Database tables (see DESIGN-NOTES, "Data model") | Seed data above |
