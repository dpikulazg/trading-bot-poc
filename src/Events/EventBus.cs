namespace CryptoTradingBot.Events
{
    /// <summary>
    /// Lightweight in-process event bus for decoupled order lifecycle management.
    /// Production: replace with MediatR, Azure Service Bus, or RabbitMQ.
    /// </summary>
    public interface IEventBus
    {
        void Subscribe<TEvent>(Func<TEvent, Task> handler)
            where TEvent : TradingEvent;
        Task PublishAsync<TEvent>(TEvent @event)
            where TEvent : TradingEvent;
    }

    public sealed class InProcessEventBus : IEventBus
    {
        private readonly Dictionary<Type, List<Delegate>> _handlers = new();
        private readonly object _lock = new();

        public void Subscribe<TEvent>(Func<TEvent, Task> handler)
            where TEvent : TradingEvent
        {
            lock (_lock)
            {
                var type = typeof(TEvent);
                if (!_handlers.ContainsKey(type))
                    _handlers[type] = [];
                _handlers[type].Add(handler);
            }
        }

        public async Task PublishAsync<TEvent>(TEvent @event)
            where TEvent : TradingEvent
        {
            List<Delegate> handlers;
            lock (_lock)
            {
                if (!_handlers.TryGetValue(typeof(TEvent), out var h))
                    return;
                handlers = h.ToList();
            }

            foreach (var handler in handlers)
            {
                try
                {
                    await ((Func<TEvent, Task>)handler)(@event);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[EventBus] Handler error for {typeof(TEvent).Name}: {ex.Message}"
                    );
                }
            }
        }
    }

    // ──────────────────────────────────────────────────
    //  Event Hierarchy
    // ──────────────────────────────────────────────────

    public abstract record TradingEvent(DateTime Timestamp, string Symbol);

    public record SignalGeneratedEvent(
        DateTime Timestamp,
        string Symbol,
        string SignalType,
        decimal Confidence,
        string Reason
    ) : TradingEvent(Timestamp, Symbol);

    public record OrderPlacedEvent(
        DateTime Timestamp,
        string Symbol,
        string OrderId,
        string Side,
        decimal Quantity,
        decimal Price
    ) : TradingEvent(Timestamp, Symbol);

    public record OrderFilledEvent(
        DateTime Timestamp,
        string Symbol,
        string OrderId,
        decimal Quantity,
        decimal Price,
        decimal Slippage
    ) : TradingEvent(Timestamp, Symbol);

    public record PositionOpenedEvent(
        DateTime Timestamp,
        string Symbol,
        decimal Quantity,
        decimal EntryPrice,
        decimal StopLoss
    ) : TradingEvent(Timestamp, Symbol);

    public record TakeProfitTriggeredEvent(
        DateTime Timestamp,
        string Symbol,
        int TierNumber,
        decimal TargetPercent,
        decimal SellQuantity,
        decimal Price
    ) : TradingEvent(Timestamp, Symbol);

    public record TrailingStopMovedEvent(
        DateTime Timestamp,
        string Symbol,
        decimal OldStop,
        decimal NewStop,
        decimal HighWaterMark
    ) : TradingEvent(Timestamp, Symbol);

    public record StopLossTriggeredEvent(
        DateTime Timestamp,
        string Symbol,
        decimal StopPrice,
        decimal ExitPrice,
        decimal PnL
    ) : TradingEvent(Timestamp, Symbol);

    public record PositionClosedEvent(
        DateTime Timestamp,
        string Symbol,
        decimal Quantity,
        decimal ExitPrice,
        decimal TotalPnL,
        string Reason
    ) : TradingEvent(Timestamp, Symbol);

    public record CircuitBreakerTrippedEvent(
        DateTime Timestamp,
        string Symbol,
        string Reason,
        TimeSpan Cooldown
    ) : TradingEvent(Timestamp, Symbol);

    public record KillSwitchActivatedEvent(DateTime Timestamp, string Symbol, string Reason)
        : TradingEvent(Timestamp, "SYSTEM");
}
