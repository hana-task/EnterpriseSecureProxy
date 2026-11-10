# Design Notes

> Work in progress. Each section is written when the corresponding part is built.

## Interpretation of unclear requirements

The request example in section 4 of the assignment is garbled. Only the following is stated: the endpoint is
`POST /api/proxy/execute`, the operation is found by `clientId`, and "Json must contain the urlId key".

Our interpretation of the request contract:

```json
{ "clientId": "customer.get", "json": { "urlId": "crm-customers", "customerId": "123" } }
```

- `clientId` is the **operation key**, not the identity of the calling client.
- `urlId` is the **name of a configured endpoint**. It is never an address. Each operation has exactly one
  `UrlId` in configuration, and the request is rejected unless the value matches exactly. The name `urlId`
  ("id of a URL") suggests this: instead of sending an address, the caller sends the name of an address that
  only the proxy knows. The real address is built solely from configuration (destination base URL plus the
  operation's path template).
- All other fields in `json` are parameters, validated against the operation's parameter rules.

## Data model

| Table | Purpose |
|---|---|
| `Destinations` | Upstream APIs the proxy may call: base URL, enabled flag, timeout, max response size. |
| `Operations` | The allowlist. Key, `UrlId`, destination, HTTP method, path template, enabled flag, max request size, encryption requirement, unknown-parameter policy, `rowversion`. |
| `ParameterRules` | Allowed parameters per operation: name, location (path/query/body), type, required, max length, numeric range. |
| `HeaderRules` | Incoming headers that may be forwarded per operation. Default is deny. |
| `EncryptionProfiles` | Algorithm and a **key reference** (a name in the secret store). No key material. |
| `OperationalSettings` | Proxy-wide name/value settings (global max request size, configuration cache duration). |
| `RequestLogs` | One audit row per request (see the audit section). |

Integrity is enforced in the database as well as in code (defence in depth):

- Unique keys: destination name, operation key, parameter name per operation, header name per operation.
- Check constraints: HTTP method in `GET/POST/PUT/PATCH/DELETE`, an operation that requires encryption must
  reference a profile, positive size limits, timeout between 1 and 300 seconds.
- A destination that still has operations cannot be deleted (disable it instead). Rules are deleted with
  their operation.
- `RequestLogs` has no foreign keys on purpose: the audit trail must survive configuration changes and must
  also record requests for operations that do not exist.

### Concurrency token

`Destinations` and `Operations` have a SQL Server `rowversion` column. SQL Server changes it automatically on
every update. An update must send the version it read; if the row changed in the meantime, the update is
rejected and the API returns 409 Conflict.

### Configuration history (assignment 5.7)

All configuration tables are **SQL Server system-versioned temporal tables**. On every update or delete,
SQL Server copies the previous version of the row to a history table (`OperationsHistory`, ...) with the
period during which it was valid (`PeriodStart`, `PeriodEnd`). This answers "which configuration changed and
when" without application code, and the history cannot be bypassed by a code path that forgets to record it.
EF Core can query it (`TemporalAll`, `TemporalAsOf`).

No secrets are stored in configuration tables (keys are referenced by name only), so none reach the history.

Limitation: the history records *what* and *when*, not *who*. With an identity provider in place, an
`UpdatedBy` column would be added and versioned automatically.

### Considered and left out

| Item | Reason |
|---|---|
| Per-operation timeout | The assignment asks for a timeout on the destination *or* operation; the destination timeout is used. |
| Permitted ports per destination | The port is part of the configured base URL, and the final URL is checked to match the base URL's scheme, host and port. A port list edited by the same administrator would not add protection. Base URLs are validated (absolute, http/https, no credentials) when configuration is saved. |
| Regex validation rule for parameters | Type, length and range cover the requirement. Path traversal is handled when building the URL. |
| Authentication profile per destination | Optional in the assignment. It would hold a *reference* to credentials the proxy presents to an upstream that requires authentication (the caller's `Authorization` header is never forwarded). |

### Sample data

The migration seeds three destinations (one disabled), one encryption profile, and five operations, each
showing one scenario: a plain request with a path parameter (`customer.get`), query parameters with unknown
parameters allowed (`customer.search`), an encrypted POST (`payment.create`), a disabled operation
(`customer.export`), and an operation on a disabled destination (`invoice.get`).
Automated tests create their own data.

## Reading configuration and caching

The request pipeline never touches EF entities directly. It asks `IPolicyProvider` for an `OperationPolicy`:
an immutable copy of one operation together with its destination, parameter rules, allowed headers and
encryption key reference.

- `PolicyRepository` (in `Data/Repositories`) reads it from SQL Server in one query (`AsNoTracking`,
  projected straight into the policy records). It is a purpose-specific repository, not a generic one:
  `DbContext` already provides the generic repository and unit-of-work behaviour.
- Operation keys are matched **exactly** (ordinal). SQL Server's default collation is case-insensitive, so the
  repository re-checks the key in code; `Customer.GET` does not resolve to `customer.get`.
- `PolicyProvider` puts a cache in front of the repository (cache-aside). `PolicyCache` is a singleton that
  owns a dedicated `MemoryCache`, so clearing it affects configuration only.
- Cached objects are immutable records, because one cached instance is shared by many concurrent requests.

Service lifetimes: the repository and provider are scoped (they depend on the scoped `DbContext`); the cache is
a singleton. Making the provider a singleton would capture one `DbContext` for the lifetime of the application.

### Consistency model

| Change | When it takes effect |
|---|---|
| Made through the admin API | Immediately on the instance that handled it (the whole cache is cleared). |
| Made on another instance, or directly in SQL | Within `ConfigurationCacheSeconds` (default 30 s, stored in `OperationalSettings`). |

- Entries expire after a fixed time (absolute expiration), so stale data has a hard upper bound.
- `ConfigurationCacheSeconds = 0` turns caching off (every request reads the database).
- Unknown operation keys are **not cached**. The key is caller-controlled, and caching "not found" would let
  a caller fill the memory with random keys. Repeated lookups of unknown keys are limited by rate limiting.
- Known race: a request that read the database just before an admin update may put the old value back into the
  cache after it was cleared. The staleness is still bounded by the expiration time. A multi-instance
  deployment that needs immediate consistency would use a distributed cache or a pub/sub invalidation message.
- A missing or invalid operational setting falls back to a safe, restrictive default and is logged, so a bad
  row can never disable a limit.

## Checks before forwarding

Every request passes the checks in `Validation/` before anything is sent. Building the outgoing request (`UpstreamRequestBuilder`, `UpstreamBodyBuilder`, `UpstreamRequest`) lives in `Forwarding/`, next to the sending code. `RequestValidator` runs the checks in a
fixed order, one call after another, and stops at the first failure. It returns a `PreForwardResult`: either an
`UpstreamRequest` ready to send, or a `Rejection`. The request never reaches the destination unless all checks pass.

```
RequestValidator
  1. EnvelopeCheck           content type, size, valid JSON, only "clientId" and "json"
     (fetch OperationPolicy by clientId)
  2. OperationCheck          operation exists, enabled, destination enabled, size fits the operation
  3. UrlIdCheck              urlId equals the configured one; removed from the remaining fields
  4. DecryptionCheck         decrypt if the operation requires it; produces the parameters
  5. ParameterCheck          required, type, length, range, unknown parameters
  6. UpstreamRequestBuilder  method, address and body from configuration + validated parameters
  7. HeaderFilter            which caller headers are forwarded
```

The checks are independent static classes: same code for every operation, driven by the `OperationPolicy` read
from configuration. They are cheap-first: checks on the request itself, then configuration, then decryption
(the parameters are inside the encrypted payload, so decryption must precede parameter validation).

### Rejections are values, not exceptions

A check returns `CheckResult<T>`: passed (with a value for the next check) or rejected (a `RejectionReason` and
a fixed, safe message). A bad request is an expected outcome, not an exceptional one, and a returned value cannot
be ignored by the caller the way a forgotten `catch` can. A genuine bug or an unavailable database still throws;
the controller (stage 6) turns that into a generic 500, so the request is not forwarded (fail closed).
`PreForwardResult` also carries the operation key and policy known so far, so a rejected request can be audited.
Mapping reasons to HTTP responses is done in stage 6; the messages never contain caller-supplied values.

### Decisions

| Topic | Decision |
|---|---|
| Invalid HTTP method (test 6) | The endpoint accepts POST only (other methods get 405 from the framework). The method sent upstream comes only from configuration and must be one of GET/POST/PUT/PATCH/DELETE (database check constraint, re-checked in code; otherwise `ProxyMisconfigured`). A `method` or `url` field sent by the caller is just an unknown field and is rejected. |
| Unknown top-level fields | Only `clientId` and `json` exist. "The request contract may be extended" is about us extending it, not the caller. |
| Duplicate JSON fields and invalid text | Rejected (also inside `json` and inside the decrypted payload), so every system reads the same request. Invalid text such as `"\uD800"` (legal JSON syntax, not a valid character) is rejected up front, because reading it later would throw. |
| Encrypted operation format | `json` = `{ urlId, nonce, ciphertext, tag }`: `urlId` stays visible ("Json must contain the urlId key"), the parameters are encrypted. A plain request to an encrypted operation is rejected, otherwise encryption could be skipped. Exactly those three fields are accepted. |
| Decryption failures | Tag/key/operation mismatch, malformed payload and non-JSON plaintext are separate reasons internally (audit) but return one identical message, so the caller cannot tell which step failed. A missing key or profile is `ProxyMisconfigured`. The size limit is applied before decrypting. The raw decrypted bytes are wiped right after parsing; the parsed values remain in memory until the request ends, since they must be forwarded. |
| Unknown parameters | Rejected by default. If the operation allows them, they do not cause a rejection but are **not forwarded**: only validated parameters reach the destination. Forwarding them (as query or body) is a possible extension, with a configured location and a length limit. |
| Parameter types | Integer accepts a JSON number or a string of digits (`"123"`); `" 12"`, `"1e3"`, `1.5` are rejected. Boolean accepts only JSON `true`/`false`. Values are re-formatted to one canonical form (`"007"` becomes `7`) before they are placed in the request. Names are case-sensitive. A path parameter is always required. |
| Path traversal (test 9) | Three layers: a numeric parameter cannot contain `..`; a path value may not be `.`/`..`, contain `/`, `\`, `%` or control characters; the value is escaped so it stays one path segment; finally the built URL must keep the destination's scheme, host, port and base path (compared with a trailing `/`, so `/api` does not accept `/api-admin`). The path template must start with `/` (otherwise text like `@host` could change the host) and may not contain `?` or `#` (the query string is built only from query parameters). |
| Request body | Built from scratch out of the validated body parameters (JSON writer escapes text). The caller's body is never copied. A GET operation cannot have body parameters. |
| Headers (test 10) | A header is forwarded only if it is on the operation's allowlist **and** not on a fixed deny list in code (Authorization, Cookie, hop-by-hop headers, Expect, Host, and every `Content-*` header, because the proxy sends a body it built itself) **and** not named in `Connection`. The deny list wins over the database, so a wrong row cannot leak credentials. Headers are filtered, not rejected, because every HTTP client sends headers the operation does not need. Values containing line breaks are dropped. |

### Known limitations

- Checks on the configuration itself (allowed method, base URL, template shape, destination timeout range and
  response limit, a `{placeholder}` without a
  path rule, body parameters on GET, the encryption algorithm) run on every request, as defence in depth.
  A bad row therefore shows up only at request time, as `ProxyMisconfigured`. The admin update (stage 9) is the
  place to reject such a row when it is saved.
- On an operation cache miss the settings are read twice (once by the validator, once by the provider to know
  the cache duration). Both reads are normally served from the cache, so this was left as is.

### Service registration

Interfaces exist where an implementation is swapped or faked in tests: `IPolicyRepository`, `IPolicyProvider`,
`IKeySource`. Other classes are concrete (one implementation). The checks are static and need no registration.
`RequestValidator` is scoped because it depends on the scoped provider.

## Forwarding to the destination

After the checks pass, `UpstreamForwarder` sends the `UpstreamRequest` and `UpstreamResponseWriter` streams the
answer back. Neither throws for expected failures; they return a `ForwardOutcome`, like the checks return values.

```
UpstreamForwarder.SendAsync   IHttpClientFactory client -> HttpRequestMessage -> response headers only
  -> ForwardResult            Responded | Timeout | ConnectionFailed | CallerCancelled
UpstreamResponseWriter        status code + filtered headers + body copied in chunks, size-limited
  -> ForwardOutcome           Responded | ResponseTooLarge | Timeout | ConnectionFailed | CallerCancelled
```

Mapping to HTTP (done in stage 6): `Timeout` -> 504, `ConnectionFailed` and `ResponseTooLarge` -> 502,
`CallerCancelled` -> nothing is sent (nobody is waiting; only audited). An HTTP error from the destination
(4xx/5xx) is `Responded`: its status code and body are passed on, marked with `X-Proxy-Source: upstream`, so a
caller can tell a destination answer from a proxy-generated failure.

### Decisions

| Topic | Decision |
|---|---|
| HttpClient | One named client from `IHttpClientFactory` (pooled connections, rotated so DNS changes are noticed). Its own `Timeout` is infinite because the timeout is per destination and must be distinguishable from a caller cancel. Redirects are not followed (`AllowAutoRedirect = false`): a destination must not be able to send the proxy to another host (SSRF). A 3xx is passed on as the destination's answer, without its `Location` header. The handler also adds no tracing headers (`traceparent` etc.): .NET adds them by default, with a trace id that can come from the caller, which would bypass the header allowlist. Both settings live in `UpstreamForwarder.CreatePrimaryHandler`. |
| Timeout (test 13) | `Destination.TimeoutSeconds`, applied with a linked `CancellationTokenSource` (caller's `RequestAborted` + `CancelAfter`). It covers the headers **and** the body: a destination that answers and then stalls is cut off too. There is no per-operation timeout (left out on purpose, see the data model). |
| Caller cancel vs timeout (tests 13, 15) | Both surface as `OperationCanceledException`. The caller's own token decides: cancelled means the caller left (`CallerCancelled`), otherwise the destination timed out (504). The linked token is passed to `SendAsync` and to every body read, so the cancel reaches the outgoing call. |
| Response size limit | `Destination.MaxResponseBytes`. A declared `Content-Length` over the limit is refused before anything is sent. Otherwise the bytes are counted while streaming (a destination may not declare a size, or may lie). Status and headers are written only when the first chunk arrives, so an early failure still lets the controller send a clean error. If the limit is exceeded after sending started, the connection is aborted: a broken response is better than a truncated one that looks complete. |
| Streaming | `ResponseHeadersRead` and an 8 KB copy loop: the body is never loaded into memory as a whole. |
| Response headers | Allowlist in code (`Content-Type`, `Content-Encoding`, `Content-Language`, `Cache-Control`, `ETag`, `Last-Modified`, `Retry-After`). Everything else is dropped: `Set-Cookie`, `Server`, `X-Powered-By`, `Location`, hop-by-hop headers. `Content-Length` is not copied because the proxy's own connection frames the body. |
| Retries (decision 6) | One extra attempt, only for `GET` and only after a connection failure, within the same timeout. A `GET` changes nothing at the destination, so repeating it cannot duplicate an action. `POST`/`PUT`/`PATCH`/`DELETE` are never repeated: the connection may drop after the destination already did the work. Timeouts (the destination may still be working) and HTTP error statuses (the destination answered; repeating adds load to a struggling system) are not retried. |

### Idempotency keys (explanation, not implemented)

To retry a non-idempotent call safely, the caller sends a unique key with each logical operation and the
destination remembers it: a second request with the same key returns the stored result instead of acting again.
This needs support at the destination, so the proxy only forwards such a key if an operation allows that header.

### Known limitations

- A connection failure on `GET` is retried once even if the connection dropped after the destination received the
  request. This is safe only because `GET` is idempotent; an operation configured as `GET` that changes data at
  the destination would be repeated. The configuration owner is responsible for the method matching the effect.
- A destination that is down costs a failed request twice per `GET`. A circuit breaker (stage 10) limits that.

## Endpoint and error responses

`POST /api/proxy/execute` is handled by a thin `ProxyController` that hands the `HttpContext` to `ProxyService`.
The service runs the flow and writes the answer itself, because the proxy works on raw streams: the checks read
the request body (size, duplicate fields, encrypted payload) and the destination's response is streamed through.
For the same reason the action has no model parameter: automatic model validation (`[ApiController]`) would
answer with its own 400 format before our checks run.

```
ProxyService.ExecuteAsync
  RequestValidator      rejected -> proxy error (nothing is sent)
  UpstreamForwarder     Responded -> UpstreamResponseWriter streams the destination's answer
  decision              Responded / CallerCancelled / response already started -> write nothing more
                        otherwise -> proxy error (502 / 504)
```

### Error format

Every proxy-generated error is `application/problem+json` (RFC 7807 ProblemDetails) with the header
`X-Proxy-Source: proxy`; a destination's answer carries `X-Proxy-Source: upstream`. The destination cannot fake
the proxy marker, because its headers pass an allowlist.

```json
{ "status": 404, "title": "Unknown operation", "detail": "The requested operation does not exist.",
  "errorCode": "unknown_operation", "correlationId": "..." }
```

- `errorCode` is fixed and documented, so callers do not parse messages.
- `detail` is the rejection's safe message: never a value sent by the caller, a hostname, configuration or
  encryption detail.
- `correlationId` is the request's correlation id (see "Audit and logging"), also returned in `X-Correlation-ID`.
- The response is cleared before an error is written, so no header set earlier can leak into it.

### Mapping (`ProxyErrorCatalog`, the single place for it)

| Failure | HTTP | errorCode |
|---|---|---|
| Invalid content type | 415 | `invalid_content_type` |
| Request too large (global or per operation) | 413 | `request_too_large` |
| Malformed envelope, or a body the server cannot read | 400 | `malformed_request` |
| Unknown operation | 404 | `unknown_operation` |
| Disabled operation | 403 | `operation_disabled` |
| Disabled destination | 503 | `destination_unavailable` |
| `urlId` missing or not matching | 400 | `invalid_url_id` |
| Encryption required but payload is plain | 400 | `encryption_required` |
| Invalid encrypted payload or failed decryption (one public code) | 400 | `invalid_encrypted_payload` |
| Missing / invalid / unknown parameter, invalid path value | 400 | `missing_parameter`, `invalid_parameter`, `unknown_parameter`, `invalid_path_value` |
| Stored configuration is wrong (not the caller's fault) | 500 | `proxy_misconfigured` |
| Destination timeout | 504 | `upstream_timeout` |
| Destination unreachable | 502 | `upstream_unreachable` |
| Destination response over its limit | 502 | `upstream_response_too_large` |
| Destination answered (any status, also 4xx/5xx) | destination's status | none: the answer is passed on |
| Caller disconnected | nothing is sent | none (audited as `CallerCancelled`) |
| Unexpected exception | 500 | `internal_error` |
| Audit record cannot be saved before forwarding | 503 | `audit_unavailable` |
| Stale configuration update | 409 | `concurrency_conflict` (admin endpoint, stage 9) |

A "missing destination" cannot occur: every operation has a required foreign key to a destination.

The two decryption failures share one public code so the caller learns nothing about which part failed; the
exact reason is kept for the audit log. A unit test walks every `RejectionReason`, so a new reason without a
mapping fails the tests.

### Failures after the response started

The destination's status and headers are written only when its first body bytes arrive. If the forwarding fails
before that, a clean 502/504 is still possible. If part of the body was already sent, the writer aborts the
connection and nothing more is written: the caller sees a broken response instead of a truncated one that looks
complete.

### Unexpected exceptions

`UseExceptionHandler` is first in the pipeline and calls `UnhandledExceptionHandler`, which writes a generic 500
`internal_error` in the same format. The middleware logs the full exception to the internal log only, skips the
handler when the response has already started (the connection is aborted) and when the caller cancelled. A caller
that disconnects during the checks is not an error: `ProxyService` catches that cancellation and writes nothing.
The `Server` response header is turned off so the server software is not announced.

### Known limitations

- 404 (wrong route) and 405 (a method other than POST) are produced by ASP.NET routing, without the ProblemDetails
  body or `X-Proxy-Source`. `UseStatusCodePages` could unify them; it was left out because they are outside the
  proxy endpoint.
- When the configuration database is unreachable the request fails closed with 500; the audit behaviour for that
  case is defined in the audit section.

## Audit and logging (assignment 5.6, 11)

There are two logs with different jobs:

| | Audit log (`RequestLogs` in SQL Server) | Technical log (Serilog) |
|---|---|---|
| Question it answers | Who asked for what, when, and was it executed? | Why did the system misbehave? |
| Written by | Our code, explicitly (`RequestAuditor`, EF Core) | Any `ILogger` call; Serilog sends it to its sinks |
| Content | The fixed fields of 5.6 only | One summary line per request, warnings, exceptions, audit fallback |
| Reliability | We know whether the save succeeded, so forwarding can depend on it | Best-effort, written in the background |
| Output | Table | Console (readable) and a daily JSON file `logs/proxy-YYYYMMDD.log`, 14 days kept |

A logging sink that writes to a database is not used for the audit: it writes in the background and in batches,
so the code cannot know that a record was stored before it forwards a request.

### Correlation id

`CorrelationIdMiddleware` runs first. It takes the caller's `X-Correlation-ID` when it is 1–64 characters of
letters, digits, `-`, `_` and `.`; otherwise it creates a new id (a Guid). The id is stored in
`HttpContext.TraceIdentifier`, so the error writer and the exception handler use it without change. It is
returned in `X-Correlation-ID` on every response (added when the response starts, after the error writer clears
the headers), put in a logging scope so every log line carries it, and saved in the audit row. It is not
forwarded to the destination. A caller-supplied id is only a search key and grants nothing; it is not unique, so
an investigation by such an id may find several rows and also uses `StartedAt`.

### Audit policy

Every request handled by `ProxyService` gets one row.

| Request | When the row is saved | If SQL Server is unavailable |
|---|---|---|
| Passes the checks (will be forwarded) | **Before** sending, with `Outcome = InProgress` and `WasForwarded = false`; the same row is updated at the end with the real outcome | **Mandatory, fail closed:** the request is not forwarded and the caller gets 503 `audit_unavailable`. If only the final update fails, the row stays `InProgress` and the full record goes to the technical log |
| Rejected, cancelled or failed before forwarding | Once, at the end | **Best-effort:** the caller gets the normal answer; the full record goes to the technical log |

Reasoning: a forwarded request may change something at the destination (a payment), so it must never run
without a trace. A rejection changed nothing, so blocking it would only reduce availability. The operational
consequence is deliberate: while SQL Server is down the proxy executes no operations (it chooses traceability over
availability), and alerting on the `audit_unavailable` error log line is needed. The configuration is also read
from SQL Server, so in practice only cached configuration keeps rejections working during an outage.

Alternatives considered: a local buffer or an outbox table (record locally, ship later) would keep the proxy
available during an outage, at the cost of a component that can itself lose data if the server fails. Left out for
the scope of this assignment.

Both saves use `CancellationToken.None`: once the request is handled it is recorded, even if the caller has left.
The final save runs in a `finally` block, so success, rejection, cancellation and unexpected exceptions are all
recorded. Saving twice adds a few milliseconds to a forwarded request, small next to the destination call.

### What a row contains

| Field | Value |
|---|---|
| `OperationKey` | From the configuration when the operation was found; otherwise the caller's text with every character other than letters, digits, `.`, `_`, `-` replaced by `?` (no line breaks or markup in logs). Null when the envelope was invalid |
| `DestinationName` | The destination's unique name (not its URL) |
| `WasForwarded` | Set only after sending, never before |
| `UpstreamStatusCode` / `ProxyStatusCode` | The destination's status when it answered / what the caller got (null when the caller left) |
| `Outcome` | `Success`, `Rejected`, `UpstreamError` (4xx/5xx or response too large), `UpstreamTimeout`, `UpstreamUnavailable`, `CallerCancelled`, `ProxyError`, or `InProgress` |
| `Reason` | A fixed code: the `RejectionReason` or `ForwardOutcome` name, `AuditUnavailable`, `UnhandledException` |

Never stored or logged: bodies, headers (so no authorization, cookies or API keys), the destination URL (it may
contain parameter values such as a customer id), parameter values, encryption keys, the rejection's message, or an
exception message. EF Core's sensitive data logging is off, so SQL parameter values are not logged either.

### Technical log content

- One line per request (`UseSerilogRequestLogging`): method, path (no query string), status, duration, plus the
  audit's `OperationKey`, `Outcome` and `Reason`, and the correlation id.
- A warning with the cause when a destination cannot be reached (the audit says only `UpstreamUnavailable`).
- Unexpected exceptions with full detail (internal log only).
- An error line with the whole record when an audit save fails (the fallback).
- Framework categories (ASP.NET, EF Core, HttpClient) only from warning level.

### Known limitations

- Requests that never reach `ProxyService` (an ASP.NET 404/405) are only in the technical log's summary line,
  not in `RequestLogs`.
- If the save before forwarding reaches the database but the connection drops before it is confirmed, the final
  save inserts a second row for the same request. The request is still not forwarded, so the error is on the safe
  side.
- The audit fallback in the technical log was verified by reading the log file, not by an automated test.

## Testing

Two layers, both xUnit:

- **Unit tests** check one class at a time (each check, the decryptor, the forwarder, the error mapping), with
  fakes and no database or HTTP server.
- **Integration tests** (`tests/SecureProxy.Tests/Integration`) cover the required scenarios of assignment
  section 12 through the whole application: HTTP request → controller → checks with configuration read from SQL
  Server → destination → HTTP answer.

### How the integration tests run

`ProxyApplicationFactory` (`WebApplicationFactory<Program>`) starts the real `Program.cs` in memory on a TestServer
and changes only:

| What | Production | Tests |
|---|---|---|
| Database | `SecureProxy` | `SecureProxy_Tests` on the same SQL Server: the developer's connection string with only the database name changed. It is deleted and rebuilt with the real migration once per test run, so the seed data is the starting point; a few test-only operations are added (a text path parameter, a 1-second timeout). |
| Destination | `SocketsHttpHandler` (network) | `RecordingHandler`: the "upstream" client is configured again in `ConfigureTestServices`, which runs after `Program`, so its primary handler replaces the real one. It records every request and answers as each test sets. |
| Encryption key | user-secrets / environment | A fixed test key in in-memory configuration, read through the same `ConfigurationKeySource`. |

A real SQL Server is used rather than SQLite or the EF in-memory provider: `rowversion` (assignment test 16),
temporal tables and the check constraints only exist there, and the real repository queries are exercised.
The database must be running (see README).

### Proof that a rejected request was blocked

Every rejection test asserts that the recording destination received **nothing**, before it checks the status,
`X-Proxy-Source: proxy` and the `errorCode`. A status code alone would not show that the request was stopped
before it was sent.

### What the recording destination cannot show

The `RecordingHandler` replaces the network handler, so behaviour of that handler itself is invisible to it. One
test therefore puts the production handler (`UpstreamForwarder.CreatePrimaryHandler`) back and sends to a real
local HTTP server: it checks that no redirect is followed and that only allowed headers arrive. Checking that
handler against a real server found a real defect (tracing headers added by .NET), listed in DEFECT-REPORT.

The absence of the `Server` header is a Kestrel setting; the TestServer never sends that header, so it is not
covered by a test.

### Audit tests

The audit tests (assignment test 17) send a request, read the answer to the end and then load the `RequestLogs`
row by the correlation id the caller got back. Reading to the end matters: the test server ends the response body
only when the whole pipeline has finished, so the final audit save has happened. They cover a success, a
rejection, a destination error, a timeout and an unknown operation key with unsafe characters.

SQL Server being unavailable is simulated with an EF Core `SaveChangesInterceptor` that throws on every save, in a
second copy of the app (`WithWebHostBuilder`). Reads still work, so only the audit write fails, which is exactly
the case the policy is about. Stopping the database would also break reading the configuration and the other
tests. Two tests show the policy: a valid request is not forwarded (503, the destination received nothing), and a
rejection is answered as usual (403).

The `ProxyService` unit tests give the auditor an EF Core in-memory database, only so its saves succeed without a
server; the audit rows themselves are checked against SQL Server in the integration tests.
