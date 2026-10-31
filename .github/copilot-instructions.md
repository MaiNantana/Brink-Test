# AI Assistant Development Rules

These rules govern all code generated or modified by AI assistants (GitHub Copilot, Claude, etc.) in this repository.
Generated code that does not follow them must be revised before it is accepted.

## Project context

- .NET 10, ASP.NET Core Minimal APIs, EF Core with SQL Server.
- Solution: `BrinkTest.sln`. API project: `src/BrinkTest.Api.csproj`. Tests: `test/BrinkTest.Api.Tests`.
- Out of scope (do not implement unless asked): real payment, real shipping, RabbitMQ, Redis.

## Coding standards

- Follow the Microsoft .NET coding conventions and the ASP.NET Core / EF Core best-practice guidance.
- Nullable reference types and implicit usings are enabled; do not suppress nullable warnings with `!` unless the value has already been validated.
- Use file-scoped namespaces matching the folder structure (`BrinkTest.Api.<Folder>`).
- Naming: `PascalCase` for types, methods and properties; `camelCase` for locals and parameters; `_camelCase` for private fields; async methods end with `Async`.
- Request/response DTOs live in `Contracts/`, entities in `Domain/`, persistence and migrations in `Data/`, business logic in `Application/`, route mapping in `Endpoints/`.
- Endpoints stay thin: they call an application service (e.g. `IOrderService`) and translate its result into an HTTP result. No database access or business rules in endpoints.
- Services return a result type for expected business failures instead of throwing exceptions.
- Inject `ILogger<T>` and `TimeProvider`; never call `DateTime.UtcNow` directly in business logic.
- Never expose EF entities directly from an endpoint; map to a response contract.
- Keep the solution minimal: no new NuGet packages without stating why they are needed. Pinned transitive packages in the `.csproj` exist to patch known vulnerabilities; do not remove them.

## Data access

- All database calls must be asynchronous and accept the request `CancellationToken`.
- Use `AsNoTracking()` for read-only queries.
- Operations that change more than one row (stock + order) must run inside one transaction.
- Stock decrements must be guarded in the database (`WHERE StockQuantity >= @quantity`) to prevent overselling under concurrency; never read-modify-write stock in memory.
- Prices and totals are calculated on the server from current product data, never taken from the client.

## Validation and error handling

- Validate every request before touching the database; return `Results.ValidationProblem` (400).
- Expected business failures return RFC 7807 `ProblemDetails` via `Results.Problem`:
  - 404 – customer or product not found
  - 409 – product inactive, insufficient stock, or stock changed concurrently
  - 422 – request is valid but cannot be processed (e.g. total out of range)
- Unexpected exceptions are not caught in endpoints; they are handled by `UseExceptionHandler` (500 ProblemDetails).
- Do not leak exception messages, stack traces or SQL details to clients.

## Logging

- Use `ILogger` with structured message templates (`{OrderId}`, `{ProductId}`), never string interpolation.
- `LogInformation` for successful business events (order created).
- `LogWarning` for every rejected request (validation failure, not found, inactive, insufficient stock, concurrency conflict).
- Never log personal data, secrets or connection strings.

## Testing

- Every business rule must have at least one test; new behaviour requires a new test.
- Test naming: `MethodOrFeature_Scenario_ExpectedResult` (e.g. `CreateOrder_UnavailableProduct_ReturnsConflictAndDoesNotCreateOrder`).
- Use xUnit with Arrange / Act / Assert structure.
- Unit tests (`OrderServiceTests`) test services directly with in-memory SQLite, `FakeTimeProvider` and `FakeLogger`. Integration tests (`OrdersEndpointTests`) cover the HTTP pipeline via `OrdersApiFactory`.
- Use SQLite, not the EF Core InMemory provider, because it does not support transactions or `ExecuteUpdateAsync`.
- Tests must assert side effects (stock quantities, order count), not only the HTTP status code.
- Tests must be isolated: each test creates its own database.
- `dotnet build` must have no warnings and `dotnet test` must pass before code is accepted.

## Review checklist for AI-generated code

Before accepting a suggestion, the developer verifies:

1. It compiles and all tests pass.
2. It follows the rules above (async, transaction, ProblemDetails, logging, naming).
3. Edge cases are handled: null/empty items, duplicate product IDs, zero/negative quantities, overflow.
4. No unnecessary abstractions, packages or files were added.
5. No hard-coded secrets or connection strings.

## Maintaining these rules

- When a review finds a recurring problem in AI output, add a rule here so later generations avoid it.
- Reference this file in prompts (e.g. "follow `.github/copilot-instructions.md`") when asking for new code or changes.
