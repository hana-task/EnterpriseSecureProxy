# AI Usage Declaration

Tool: **Claude Code** (Anthropic; Claude Opus 5.5 and Claude Sonnet 5.5 models) inside VS Code.

I remain responsible for the full solution. I reviewed, ran and understood all AI-generated code and can explain each design decision.

| Area | How AI was used |
|---|---|
| Requirements analysis | Clarifying the (partly garbled) assignment document, identifying missing items, drafting clarification questions, planning and effort estimation. |
| Work planning | Splitting the work into stages with time estimates. I reviewed and adjusted the plan. |
| Environment setup | Installing the .NET 8 SDK, running SQL Server in Docker, scaffolding the solution. |
| Encryption component | Generating the AES-GCM decryptor, the key-source abstraction and unit tests. Design choices (AAD bound to operation key, fail-closed key resolution, typed failures) were discussed and reviewed. |
| Data model | Proposing the schema mapped to assignment sections 3 and 5.1–5.7. I chose the simplifications and the interpretation of `urlId`. AI generated the entities, the DbContext configuration, sample data and the migration, and verified the result in SQL Server. |
| Configuration reading and cache | Generating the repository, the immutable policy records, the cache-aside provider and its unit tests. The repository was verified against the development database. |
| Checks before forwarding | Designing the order of checks and the rejection model with me, then generating the check classes (envelope, operation, urlId, decryption, parameters, URL and body builder, header filter), the validator and their unit tests. The interpretation of the unclear requirements (method, unknown parameters, encrypted format) was decided by me after the options were explained. An AI code review of the stage found five real defects (invalid JSON text causing an exception, a base-path prefix check, `?`/`#` in path templates, body-describing headers, differing decryption messages); they were fixed with tests and are listed in DEFECT-REPORT. |
| Forwarding to the destination | Designing the structure with me (forwarder, result type, response writer, header filter), then generating the code and its unit tests with a recording fake destination: `IHttpClientFactory` client, per-destination timeout with caller-cancel vs timeout distinction, streaming with a response size limit, response header allowlist, and the retry policy. I chose the retry option (one retry, GET only, connection failures only) after the alternatives were explained. During development a fake response stream in a test ignored cancellation and hung the test run; I fixed the test double (not a product defect). An AI code review of the stage against assignment section 9 found one real defect (an out-of-range destination timeout threw inside the forwarder and leaked its cancellation source; a non-positive response limit was noticed only after the destination had acted). It was fixed with tests and is listed in DEFECT-REPORT; the review also added missing tests for the response writer. |
| Endpoint and error handling | Planning the structure with me, generating the code and unit tests, and a code review against assignment section 10. I chose the status codes after the options were explained. |
| Integration tests | Planning the test setup with me, generating the integration tests for the required scenarios and a review against assignment section 12. |

<!-- Keep appending rows as work progresses. -->
