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

- Checks on the configuration itself (allowed method, base URL, template shape, a `{placeholder}` without a
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
| HttpClient | One named client from `IHttpClientFactory` (pooled connections, rotated so DNS changes are noticed). Its own `Timeout` is infinite because the timeout is per destination and must be distinguishable from a caller cancel. Redirects are not followed (`AllowAutoRedirect = false`): a destination must not be able to send the proxy to another host (SSRF). A 3xx is passed on as the destination's answer, without its `Location` header. |
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
