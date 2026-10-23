# database/

Index of where the PostgreSQL schema, migrations, and seed data actually live.

This directory holds **no SQL files**. FactoryBrain manages its schema with
**EF Core code-first migrations** compiled into the backend. EF Core loads
them from the assembly that contains `FactoryBrainDbContext`, so they have to
stay with that project. Copies here would only drift out of date.

## Where things are

| Concern | Location | Notes |
|---|---|---|
| Schema (model) | [`FactoryBrain.Backend/FactoryBrain.Infrastructure/Persistence/FactoryBrainDbContext.cs`](../FactoryBrain.Backend/FactoryBrain.Infrastructure/Persistence/FactoryBrainDbContext.cs) | Plus Fluent API configs in `Persistence/Configurations/`. Registers the `vector` (pgvector) extension. |
| Migrations | [`FactoryBrain.Backend/FactoryBrain.Infrastructure/Migrations/`](../FactoryBrain.Backend/FactoryBrain.Infrastructure/Migrations/) | `InitialBaseline`, `AddEmbeddingMetadata`, `AddIngestMetadata`, `MakeDocumentChunkEmbeddingNullable`, `AddUsers`, plus the model snapshot. |
| Seed data | [`FactoryBrain.Backend/FactoryBrain.Infrastructure/Seed/DbInitializer.cs`](../FactoryBrain.Backend/FactoryBrain.Infrastructure/Seed/DbInitializer.cs) | Idempotent C# seeder. It mirrors the frontend demo datasets in `frontend/src/data/*.ts`. |
| Startup behaviour | [`FactoryBrain.Backend/FactoryBrain.Api/Program.cs`](../FactoryBrain.Backend/FactoryBrain.Api/Program.cs) | On startup the API runs `CREATE EXTENSION IF NOT EXISTS vector`, applies pending migrations (`Database.Migrate()`), then runs `DbInitializer`. |

## Connection

The API resolves its connection string in this order (see `Program.cs`):

1. `FACTORYBRAIN_DB` environment variable
2. `DOTNET_CONNECTION_STRING` environment variable
3. `ConnectionStrings:Postgres` in `appsettings.json`
4. A hard-coded `localhost` / `postgres:postgres` fallback. This is a known
   issue: see `docs/SECURITY_ANALYSIS.md` F-1.1 / F-1.2.

Keep real credentials in an untracked `.env` or in the host's secret store,
never in tracked files. See [`docs/ENVIRONMENT.md`](../docs/ENVIRONMENT.md).

PostgreSQL's runtime data directory does **not** belong in this repository.
Run Postgres as a service or container (the root `README.md` shows a
`pgvector/pgvector` Docker command).

## Common commands

Run these from `FactoryBrain.Backend/`. They need the EF Core CLI
(`dotnet tool install -g dotnet-ef`).

```bash
# Add a migration after changing the model
dotnet ef migrations add <Name> \
  --project FactoryBrain.Infrastructure --startup-project FactoryBrain.Api

# Produce an idempotent SQL script, e.g. for a DBA review (not committed)
dotnet ef migrations script --idempotent \
  --project FactoryBrain.Infrastructure --startup-project FactoryBrain.Api \
  -o ../database/generated-schema.sql
```

If you start committing generated SQL here, add `migrations/`, `schema/`, or
`seeds/` subfolders at that point, and say which file is the source of truth.
