# AI Usage Declaration

Tool: **Claude Code** (Anthropic; Claude Opus 5.5 and Claude Sonnet 5.5 models) inside VS Code.

## Ownership

This is my solution, and I am responsible for all of it. AI was used as a development tool under my direction:
no code was added without my reviewing it. I went over every change, asked questions about what I did not
understand, commented and asked for changes, and ran the tests and the application myself. I can explain,
trace and modify every part of the code.

## How I worked with Claude Code

1. **Planning first.** Before writing code, I used the AI to analyse the assignment, map every requirement to a
   stage and a test, and list the unclear points. The plan, with time estimates and a decision log, was kept in
   a working document throughout the project.
2. **Splitting into stages.** The work was split into small stages (database, configuration, checks, forwarding,
   errors, tests, audit, admin API, resilience, documentation). Each stage started with a short plan that I
   approved or changed before any code was written.
3. **I made the decisions.** For each open question (interpretation of the requirements, status codes, audit
   policy, retry policy, what to implement and what only to explain), the AI presented the options with a
   recommendation, and I chose.
4. **Two models.** I used Claude Opus 5.5 for planning and code review, and Claude Sonnet 5.5 for writing code.
5. **Code review after each stage.** Each stage ended with the tests passing and a code review (with Claude Code's
   code-review skill) against the relevant assignment sections. Some behaviour was also checked against the
   running application and a real SQL Server. The real defects these reviews found were fixed with tests and are
   listed in DEFECT-REPORT.md.
6. **Reading the code myself.** I read the code of each step in the editor, traced requests through it with the
   debugger, and discussed the design choices until I could explain them in my own words. Where I disagreed
   with something, I asked for it to be changed.

## What AI was used for

| Area | How AI was used |
|---|---|
| Requirements analysis and planning | Clarifying the assignment, identifying unclear items, splitting the work into stages with time estimates. I reviewed and adjusted the plan. |
| Environment setup | Installing the .NET 8 SDK, running SQL Server in Docker, scaffolding the solution. |
| Encryption | Generating the AES-GCM decryptor, the key-source abstraction and their tests. The design choices (ciphertext bound to the operation through the AAD, fail closed on a missing key, typed failures) were discussed and reviewed. |
| Data model | Proposing the schema for assignment sections 3 and 5, then generating the entities, EF Core configuration, migration and seed data. I chose what the schema includes and the interpretation of `urlId`. |
| Configuration reading and caching | Generating the repository, the immutable configuration records, the cache and their tests. |
| Checks before forwarding | Designing the order of checks and the rejection model with me, then generating the checks and their tests. I decided the interpretation of the unclear requirements (method, unknown parameters, encrypted format). |
| Forwarding | Designing the forwarder with me and generating it with its tests: `IHttpClientFactory`, timeouts, cancellation, streaming, size limits, response headers. I chose the retry policy. |
| Endpoint and error handling | Generating the controller, the error format and mapping, and their tests. I chose the status codes. |
| Integration tests | Planning the test setup with me and generating the tests for the required scenarios, with a recording fake destination. |
| Audit and logging | Planning the audit policy with me and generating the correlation id, Serilog setup, audit recorder and tests. I chose the policy. |
| Configuration updates | Generating the admin API with rowversion conflict handling, cache clearing and tests. |
| Resilience | Generating the rate limiting and concurrency limit with tests, and writing the circuit breaker explanation. I chose what to implement and what to explain. |
| Documentation | Drafting README, DESIGN-NOTES and DEFECT-REPORT from the decisions made during the work. I reviewed them. |
