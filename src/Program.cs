using BrinkTest.Api.Application.Orders;
using BrinkTest.Api.Data;
using BrinkTest.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<OrdersDbContext>((services, options) =>
{
    var connectionString = services.GetRequiredService<IConfiguration>()
        .GetConnectionString("OrdersDb")
        ?? throw new InvalidOperationException(
            "Connection string 'OrdersDb' is required. Set ConnectionStrings__OrdersDb.");

    options.UseSqlServer(connectionString);
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapOrdersEndpoints();

app.Run();

public partial class Program;
