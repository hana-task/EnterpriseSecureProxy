# AI Usage Declaration

Tool: **Claude Code** (Anthropic, Claude Opus 5.5 model) inside VS Code.

I remain responsible for the full solution. I reviewed, ran and understood all AI-generated code and can explain each design decision.

| Area | How AI was used |
|---|---|
| Requirements analysis | Clarifying the (partly garbled) assignment document, identifying missing items, drafting clarification questions, planning and effort estimation. |
| Work planning | Splitting the work into stages with time estimates. I reviewed and adjusted the plan. |
| Environment setup | Installing the .NET 8 SDK, running SQL Server in Docker, scaffolding the solution. |
| Encryption component | Generating the AES-GCM decryptor, the key-source abstraction and unit tests. Design choices (AAD bound to operation key, fail-closed key resolution, typed failures) were discussed and reviewed. |
| Data model | Proposing the schema mapped to assignment sections 3 and 5.1–5.7. I chose the simplifications and the interpretation of `urlId`. AI generated the entities, the DbContext configuration, sample data and the migration, and verified the result in SQL Server. |
| Configuration reading and cache | Generating the repository, the immutable policy records, the cache-aside provider and its unit tests. The repository was verified against the development database. |

<!-- Keep appending rows as work progresses. -->
