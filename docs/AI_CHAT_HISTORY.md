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



