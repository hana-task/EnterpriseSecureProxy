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
