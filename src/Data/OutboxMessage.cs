namespace BrinkTest.Api.Data;

/// <summary>
/// Integration event stored in the same transaction as the business change (transactional outbox).
/// A relay publishes unprocessed rows to RabbitMQ using <see cref="Id"/> as the message id.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public required string Type { get; set; }
    public required string Payload { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
}
