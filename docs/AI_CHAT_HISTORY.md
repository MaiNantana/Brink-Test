User: Act as a Senior .NET Backend Developer. 
I need to implement a REST API endpoint: `POST /api/orders` in .NET 10 using ASP.NET Core Minimal APIs and Entity Framework Core following Microsoft Best Practices.

Requirements:
1. Input validation: Validate customer ID and order items (Quantity must be > 0).
2. Domain checks: Verify products exist, are active (`IsActive == true`), and have sufficient stock.
3. Database Transaction: Calculate total amount dynamically, decrement stock, save Order (Status: Pending) and OrderItems asynchronously within a database transaction.
4. Concurrency & Safety: Handle race conditions and ensure rollback on failure.
5. Error Handling & Logging: Proper ILogger implementation and standard HTTP ProblemDetails responses.
6. Return `201 Created` with the newly generated `OrderId` and order summary.

Please write clean, production-ready code with Entities, DTOs, DbContext, and Endpoint mapping.

Copilot: I’ll first inspect the existing project structure and conventions so the endpoint fits the repository’s .NET version, data model, and API style.

Find files matching `**/*.{cs,csproj,json}`

Ran terminal command: git status --short; Get-ChildItem -Force | Select-Object Mode,Name

Ran terminal command: git ls-files; Get-ChildItem -Force -Recurse | Select-Object -First 80 FullName,Mode

Ask User
Completed with input: {
  "message": "The repository is empty, so this needs a new Minimal API project. Which EF Core database provider should the implementation target? The transaction and guarded stock updates work best with a relational provider; SQL Server is a reasonable default.",
  "requestedSchema": {
    "properties": {
      "provider": {
        "type": "string",
        "title": "EF Core provider",
        "description": "Choose the database provider for the new .NET 10 API scaffold.",
        "enum": [
          "SQL Server",
          "PostgreSQL",
          "SQLite",
          "Provider-agnostic sample"
        ],
        "default": "SQL Server"
      }
    }
  }
}

I’ll scaffold a .NET 10 Minimal API targeting SQL Server. Stock will be decremented with a guarded database-side update inside the same transaction as the order insert, so concurrent requests cannot oversell inventory.

Ran terminal command: git status --short --branch; git log -1 --oneline
Tool execution failed with exit code 128

Find .NET Path
Completed with input: {
  "version": "10.0",
  "mode": "sdk"
}

Edit [](), [](), [](), [](), [](), [](), [](), [](), [](), [](), [](), []()

The core implementation is in place. I’m checking the generated files for consistency and obvious build issues; this environment has no .NET 10 SDK, so I’ll also clearly report that compilation couldn’t be run here.

Read []()
Completed with input: {
  "path": "C:\\dev\\brink-test\\Brink-Test\\Endpoints\\OrdersEndpoints.cs"
}

Read []()
Completed with input: {
  "path": "C:\\dev\\brink-test\\Brink-Test\\Data\\OrdersDbContext.cs"
}

Read []()
Completed with input: {
  "path": "C:\\dev\\brink-test\\Brink-Test\\Program.cs"
}

Ran terminal command: git diff --check; git status --short; Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source

Edit , []()

Edit []()

Read []()
Completed with input: {
  "path": "C:\\dev\\brink-test\\Brink-Test\\BrinkTest.Api.csproj"
}

Read []()
Completed with input: {
  "path": "C:\\dev\\brink-test\\Brink-Test\\Contracts\\CreateOrderRequest.cs"
}

Read []()
Completed with input: {
  "path": "C:\\dev\\brink-test\\Brink-Test\\Contracts\\OrderSummaryResponse.cs"
}

Read []()
Completed with input: {
  "path": "C:\\dev\\brink-test\\Brink-Test\\README.md"
}

Ran terminal command: dotnet --list-sdks; git status --short

Implemented a .NET 10 Minimal API scaffold targeting SQL Server. `POST /api/orders` validates the request and customer, checks product availability, calculates prices from the database, and saves the pending order and stock changes in one transaction. Guarded SQL updates prevent concurrent requests from overselling stock; validation and domain errors use ProblemDetails.

The implementation is in [OrdersEndpoints.cs](), with entities in [Domain/](), request and response DTOs in [Contracts/](), EF configuration in [OrdersDbContext.cs](), and startup configuration in [Program.cs](). Setup and migration instructions are in [README.md]().

I couldn’t build or run tests: this environment has .NET SDK 9.0.310, but not the .NET 10 SDK.