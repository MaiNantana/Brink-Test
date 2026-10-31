# BrinkTest Orders API

A .NET 10 Minimal API that creates orders using EF Core and SQL Server.

## Assessment deliverables

| Part | Deliverable | Location |
|---|---|---|
| 1 – System design | Order processing sequence diagram (API, SQL Server, Redis, RabbitMQ, payment, shipping) | [`docs/1-Diagram/Diagram.drawio`](docs/1-Diagram/Diagram.drawio) |
| 2 – SQL Server | Create-order transaction script | [`docs/2-Query/order.sql`](docs/2-Query/order.sql) |
| 3.1 – .NET implementation | `POST /api/orders` | [`src/`](src/) |
| 3.2 – Unit tests | Unit and integration tests | [`test/BrinkTest.Api.Tests/`](test/BrinkTest.Api.Tests/) |
| 3 – AI-assisted development | AI development rules and prompts | [`.github/copilot-instructions.md`](.github/copilot-instructions.md), [`docs/promt-first.md`](docs/promt-first.md), [`docs/copilot-build-prompts.md`](docs/copilot-build-prompts.md), [`docs/3-Testing/`](docs/3-Testing/) |

## Project structure

```text
BrinkTest.sln
├── .github/
│   └── copilot-instructions.md   Rules for AI-generated code
├── docs/
│   ├── 1-Diagram/                Part 1: order flow diagram (.drawio, .png)
│   ├── 2-Query/                  Part 2: setup.sql, create-order SQL
│   ├── 3-Testing/                Part 3: prompts for tests
│   ├── promt-first.md            Part 3: first prompt
│   └── copilot-build-prompts.md  Part 3: prompts to build src/ and test/
├── src/                          BrinkTest.Api
│   ├── Program.cs                DI and middleware
│   ├── appsettings*.json         Connection string, logging
│   ├── Endpoints/                Route mapping, result → HTTP
│   ├── Application/Orders/       OrderService, result type, event
│   ├── Contracts/                Request / response DTOs
│   ├── Domain/                   Order, OrderItem, Product, ...
│   └── Data/                     DbContext, OutboxMessage,
│                                 Migrations/
└── test/BrinkTest.Api.Tests/
    ├── OrderServiceTests.cs      Unit tests
    ├── OrdersEndpointTests.cs    Integration tests
    └── OrdersApiFactory.cs       Test host with SQLite
```

## Run

The default connection string in `src/appsettings.json` targets SQL Server LocalDB. Override it with `ConnectionStrings__OrdersDb` if needed.

```powershell
dotnet tool install --global dotnet-ef --version 10.0.0   # once
dotnet ef database update --project src
dotnet run --project src
```

The API does not manage `Customers` and `Products`; insert test rows into those tables before creating orders.

When running in the `Development` environment, Swagger UI is available at `/swagger` and the OpenAPI document at `/swagger/v1/swagger.json`.

## Test

```powershell
dotnet test BrinkTest.sln
```

| Type | File | Covers |
|---|---|---|
| Unit | `OrderServiceTests` | Calls `OrderService` directly with SQLite in-memory, `FakeTimeProvider` and `FakeLogger`. Successful order (stock, saved order, outbox message, log), insufficient stock, inactive product, validation, idempotent retry, idempotency key reused, invalid idempotency key |
| Integration | `OrdersEndpointTests` | Full HTTP pipeline via `WebApplicationFactory`. Status codes and ProblemDetails, unknown customer/product, duplicate product lines, rollback when the insert fails, idempotency over HTTP, Swagger document |

SQLite is used instead of the EF Core InMemory provider because the service relies on real transactions and `ExecuteUpdateAsync`, which InMemory does not support.

## Create an order

`POST /api/orders` with an optional `Idempotency-Key` header (1–64 characters, e.g. a client-generated GUID):

```http
POST /api/orders
Content-Type: application/json
Idempotency-Key: 3f0c2a8e-7c1d-4a55-9a51-2f3b6f4f8d10

{
  "customerId": 12,
  "items": [
    { "productId": 34, "quantity": 2 }
  ]
}
```

The request is validated first. Then, in one SQL transaction, the API:

1. checks that the customer exists and every product exists and is active;
2. reserves stock with a guarded atomic update (`WHERE StockQuantity >= @quantity`), so concurrent requests cannot oversell;
3. inserts the order with status `PendingPayment`, its items (prices taken from the database) and the idempotency key;
4. inserts an `OrderCreatedEvent` row into `OutboxMessages` (transactional outbox).

| Status | When |
|---|---|
| `201` | Order created; body contains `orderId` and the order summary |
| `200` | Retry with the same `Idempotency-Key` and same body; returns the original order, stock is not reserved again |
| `400` | Validation failure (including an invalid `Idempotency-Key`) |
| `404` | Unknown customer or product |
| `409` | Product inactive, insufficient stock, or stock changed concurrently |
| `422` | Order total exceeds the supported range, or `Idempotency-Key` reused with a different body |
| `500` | Unexpected error (handled by `UseExceptionHandler`) |

## Relation to the Part 1 design

`docs/1-Diagram/Diagram.drawio` shows the full target flow. Part 3 implements the API and SQL Server part of it:

| Design step | Part 3 |
|---|---|
| Idempotency-Key check | Implemented with a unique filtered index on `Orders.IdempotencyKey` (Redis fast-path lock not implemented) |
| Stock reservation | Implemented as the guarded SQL update, which is the authoritative check (Redis Lua pre-check not implemented) |
| Insert order `PendingPayment` + outbox | Implemented |
| Outbox relay → RabbitMQ, payment and shipping workers | Not implemented (out of scope); `OutboxMessages.Id` is the message id that consumers use for de-duplication |

> Part 2 (`docs/2-Query/order.sql`) uses the Part 2 schema, where the initial status is `Pending` as that task requires. The Part 3 API follows the Part 1 design, so its initial status is `PendingPayment`.
