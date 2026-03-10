# AI Coding Agent Instructions for property-search-nx

## Workspace Architecture
- **Monorepo managed by Nx**: Contains apps (API, web) and libs (shared contracts, types, validation).
- **API (C#/.NET)**: Located in `apps/api/PropertySearch.Api`. Contains agents, controllers, services, models, and validation logic.
- **Web (React/TypeScript)**: Located in `apps/web`. Uses Vite, with components, services, and assets.
- **Shared Libraries**: TypeScript libraries in `libs/` for contracts, types, and validation shared across projects.

## Nx Developer Workflows
- **Always use Nx CLI for builds, tests, linting, and project graph:**
  - Example: `npm exec nx build <project>`
  - Example: `npm exec nx test <project>`
  - Example: `npm exec nx graph` (visualize dependencies)
- **Scaffolding**: Use Nx generators (`nx-generate` skill) for new apps/libs.
- **Sync TypeScript project references**: Run `npm exec nx sync` after dependency changes.
- **CI/CD**: Nx Cloud is integrated for caching, task distribution, and self-healing CI.

## Project-Specific Patterns & Conventions
- **API Agents**: C# agents in `Agents/Base` implement `IAgent` and return `AgentResult`. See `DataOptimizationAgent.cs`, `NlpParserAgent.cs`.
- **Service Layer**: Business logic in `Services/` (e.g., `CraigslistScraperService.cs`, `CsvService.cs`).
- **Validation**: Centralized in `Validation/SearchValidation.cs`.
- **Web App**: React components in `src/app/components`, API calls in `src/app/services/api.ts`.
- **TypeScript Libs**: Expose types/contracts via `src/lib/` and `src/index.ts`.

## Integration & Communication
- **API <-> Web**: Web app calls API endpoints via `api.ts` service.
- **Shared Types**: Use `libs/shared-types` and `libs/shared-contracts` for cross-project type safety.
- **CSV Data**: Property data stored in `apps/api/PropertySearch.Api/Data/properties.csv`.

## Examples
- To build the web app: `npm exec nx build web`
- To run API tests: `npm exec nx test api`
- To generate a new library: `npm exec nx g @nx/js:lib libs/new-lib --importPath=@my-org/new-lib`

## References
- See `AGENTS.md` and `README.md` for Nx-specific guidance and task examples.
- API agent pattern: `apps/api/PropertySearch.Api/Agents/Base/IAgent.cs`, `AgentResult.cs`
- Web component pattern: `apps/web/src/app/components/PropertyCard.tsx`, `SearchBar.tsx`
- Shared types: `libs/shared-types/src/lib/shared-types.ts`

---

**Update this file if new conventions or workflows are introduced.**
