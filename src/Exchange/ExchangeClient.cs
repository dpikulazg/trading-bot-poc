using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoTradingBot.Configuration;
using CryptoTradingBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoTradingBot.Exchange;

/// <summary>
/// Abstraction for exchange operations - swap implementations for different exchanges.
/// </summary>
public interface IExchangeClient
{
    Task<Ticker> GetTickerAsync(string symbol, CancellationToken ct = default);
    Task<IReadOnlyList<Kline>> GetKlinesAsync(
        string symbol,
        string interval,
        int limit,
        CancellationToken ct = default
    );
    Task<Order> PlaceLimitOrderAsync(
        string symbol,
        OrderSide side,
        decimal price,
        decimal quantity,
        CancellationToken ct = default
    );
    Task<Order> PlaceMarketOrderAsync(
        string symbol,
        OrderSide side,
        decimal quantity,
        CancellationToken ct = default
    );
    Task<bool> CancelOrderAsync(
        string symbol,
        string exchangeOrderId,
        CancellationToken ct = default
    );
    Task<Order?> GetOrderStatusAsync(
        string symbol,
        string exchangeOrderId,
        CancellationToken ct = default
    );
    Task<decimal> GetBalanceAsync(string asset, CancellationToken ct = default);
}

/// <summary>
/// Binance REST API client - production-ready with HMAC signing.
/// </summary>
public sealed class BinanceClient : IExchangeClient
{
    private readonly HttpClient _http;
    private readonly ExchangeConfig _config;
    private readonly ILogger<BinanceClient> _logger;

    public BinanceClient(
        HttpClient http,
        IOptions<BotConfiguration> options,
        ILogger<BinanceClient> logger
    )
    {
        _http = http;
        _config = options.Value.Exchange;
        _logger = logger;

        _http.BaseAddress = new Uri(
            _config.UseSandbox ? "https://testnet.binance.vision" : _config.BaseUrl
        );
    }

    public async Task<Ticker> GetTickerAsync(string symbol, CancellationToken ct = default)
    {
        var json = await GetAsync($"/api/v3/ticker/24hr?symbol={symbol}", signed: false, ct);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new Ticker(
            Symbol: symbol,
            Price: decimal.Parse(root.GetProperty("lastPrice").GetString()!),
            BidPrice: decimal.Parse(root.GetProperty("bidPrice").GetString()!),
            AskPrice: decimal.Parse(root.GetProperty("askPrice").GetString()!),
            Volume24h: decimal.Parse(root.GetProperty("volume").GetString()!),
            Timestamp: DateTimeOffset.UtcNow
        );
    }

    public async Task<IReadOnlyList<Kline>> GetKlinesAsync(
        string symbol,
        string interval,
        int limit,
        CancellationToken ct = default
    )
    {
        var json = await GetAsync(
            $"/api/v3/klines?symbol={symbol}&interval={interval}&limit={limit}",
            signed: false,
            ct
        );
        var arr = JsonDocument.Parse(json).RootElement;
        var klines = new List<Kline>();

        foreach (var k in arr.EnumerateArray())
        {
            var items = k.EnumerateArray().ToArray();
            klines.Add(
                new Kline(
                    OpenTime: DateTimeOffset.FromUnixTimeMilliseconds(items[0].GetInt64()),
                    Open: decimal.Parse(items[1].GetString()!),
                    High: decimal.Parse(items[2].GetString()!),
                    Low: decimal.Parse(items[3].GetString()!),
                    Close: decimal.Parse(items[4].GetString()!),
                    Volume: decimal.Parse(items[5].GetString()!)
                )
            );
        }

        return klines;
    }

    public async Task<Order> PlaceLimitOrderAsync(
        string symbol,
        OrderSide side,
        decimal price,
        decimal quantity,
        CancellationToken ct = default
    )
    {
        var order = new Order
        {
            Symbol = symbol,
            Side = side,
            Type = OrderType.Limit,
            Price = price,
            Quantity = quantity,
        };

        var parameters = new Dictionary<string, string>
        {
            ["symbol"] = symbol,
            ["side"] = side == OrderSide.Buy ? "BUY" : "SELL",
            ["type"] = "LIMIT",
            ["timeInForce"] = "GTC",
            ["quantity"] = quantity.ToString("F6"),
            ["price"] = price.ToString("F2"),
        };

        var json = await PostSignedAsync("/api/v3/order", parameters, ct);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        order.ExchangeOrderId = root.GetProperty("orderId").GetInt64().ToString();
        order.Status = MapStatus(root.GetProperty("status").GetString()!);

        _logger.LogInformation(
            "Placed {Side} LIMIT {Qty} {Symbol} @ {Price} → OrderId: {Id}",
            side,
            quantity,
            symbol,
            price,
            order.ExchangeOrderId
        );

        return order;
    }

    public async Task<Order> PlaceMarketOrderAsync(
        string symbol,
        OrderSide side,
        decimal quantity,
        CancellationToken ct = default
    )
    {
        var order = new Order
        {
            Symbol = symbol,
            Side = side,
            Type = OrderType.Market,
            Quantity = quantity,
        };

        var parameters = new Dictionary<string, string>
        {
            ["symbol"] = symbol,
            ["side"] = side == OrderSide.Buy ? "BUY" : "SELL",
            ["type"] = "MARKET",
            ["quantity"] = quantity.ToString("F6"),
        };

        var json = await PostSignedAsync("/api/v3/order", parameters, ct);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        order.ExchangeOrderId = root.GetProperty("orderId").GetInt64().ToString();
        order.Status = MapStatus(root.GetProperty("status").GetString()!);
        if (root.TryGetProperty("fills", out var fills) && fills.GetArrayLength() > 0)
        {
            order.Price = decimal.Parse(fills[0].GetProperty("price").GetString()!);
            order.FilledQuantity = quantity;
            order.FilledAt = DateTimeOffset.UtcNow;
        }

        _logger.LogInformation(
            "Placed {Side} MARKET {Qty} {Symbol} → OrderId: {Id}",
            side,
            quantity,
            symbol,
            order.ExchangeOrderId
        );

        return order;
    }

    public async Task<bool> CancelOrderAsync(
        string symbol,
        string exchangeOrderId,
        CancellationToken ct = default
    )
    {
        try
        {
            var parameters = new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["orderId"] = exchangeOrderId,
            };
            await DeleteSignedAsync("/api/v3/order", parameters, ct);
            _logger.LogInformation(
                "Cancelled order {OrderId} for {Symbol}",
                exchangeOrderId,
                symbol
            );
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cancel order {OrderId}", exchangeOrderId);
            return false;
        }
    }

    public async Task<Order?> GetOrderStatusAsync(
        string symbol,
        string exchangeOrderId,
        CancellationToken ct = default
    )
    {
        var parameters = new Dictionary<string, string>
        {
            ["symbol"] = symbol,
            ["orderId"] = exchangeOrderId,
        };

        var queryString = BuildSignedQuery(parameters);
        var json = await GetAsync($"/api/v3/order?{queryString}", signed: true, ct);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new Order
        {
            ExchangeOrderId = exchangeOrderId,
            Symbol = symbol,
            Side = root.GetProperty("side").GetString() == "BUY" ? OrderSide.Buy : OrderSide.Sell,
            Price = decimal.Parse(root.GetProperty("price").GetString()!),
            Quantity = decimal.Parse(root.GetProperty("origQty").GetString()!),
            FilledQuantity = decimal.Parse(root.GetProperty("executedQty").GetString()!),
            Status = MapStatus(root.GetProperty("status").GetString()!),
        };
    }

    public async Task<decimal> GetBalanceAsync(string asset, CancellationToken ct = default)
    {
        var queryString = BuildSignedQuery(new Dictionary<string, string>());
        var json = await GetAsync($"/api/v3/account?{queryString}", signed: true, ct);
        var doc = JsonDocument.Parse(json);

        foreach (var balance in doc.RootElement.GetProperty("balances").EnumerateArray())
        {
            if (balance.GetProperty("asset").GetString() == asset)
                return decimal.Parse(balance.GetProperty("free").GetString()!);
        }
        return 0m;
    }

    #region HTTP Helpers

    private async Task<string> GetAsync(string path, bool signed, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (signed)
            request.Headers.Add("X-MBX-APIKEY", _config.ApiKey);
        var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        return body;
    }

    private async Task<string> PostSignedAsync(
        string path,
        Dictionary<string, string> parameters,
        CancellationToken ct
    )
    {
        var queryString = BuildSignedQuery(parameters);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{path}?{queryString}");
        request.Headers.Add("X-MBX-APIKEY", _config.ApiKey);
        var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        return body;
    }

    private async Task<string> DeleteSignedAsync(
        string path,
        Dictionary<string, string> parameters,
        CancellationToken ct
    )
    {
        var queryString = BuildSignedQuery(parameters);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{path}?{queryString}");
        request.Headers.Add("X-MBX-APIKEY", _config.ApiKey);
        var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        return body;
    }

    private string BuildSignedQuery(Dictionary<string, string> parameters)
    {
        parameters["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        parameters["recvWindow"] = "5000";
        var query = string.Join(
            "&",
            parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}")
        );
        var signature = Sign(query);
        return $"{query}&signature={signature}";
    }

    private string Sign(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_config.ApiSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static OrderStatus MapStatus(string status) =>
        status switch
        {
            "NEW" => OrderStatus.New,
            "FILLED" => OrderStatus.Filled,
            "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
            "CANCELED" or "CANCELLED" => OrderStatus.Cancelled,
            "REJECTED" => OrderStatus.Rejected,
            _ => OrderStatus.New,
        };

    #endregion
}

/// <summary>
/// MetaTrader 5 REST client - uses an MT5 bridge API for trading operations.
/// </summary>
public sealed class MetaTrader5Client : IExchangeClient
{
    private const string ApiKeyHeaderName = "X-API-KEY";
    private readonly HttpClient _http;
    private readonly ExchangeConfig _config;
    private readonly ILogger<MetaTrader5Client> _logger;

    public MetaTrader5Client(
        HttpClient http,
        IOptions<BotConfiguration> options,
        ILogger<MetaTrader5Client> logger
    )
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _http = http;
        _config = options.Value.Exchange;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_config.BaseUrl))
            throw new InvalidOperationException(
                "Exchange BaseUrl must be configured for MetaTrader 5."
            );

        _http.BaseAddress = new Uri(_config.BaseUrl);
    }

    /// <inheritdoc />
    public async Task<Ticker> GetTickerAsync(string symbol, CancellationToken ct = default)
    {
        var json = await GetAsync($"/api/v1/ticker?symbol={symbol}", ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new Ticker(
            Symbol: symbol,
            Price: GetDecimal(root, "lastPrice", "last", "price"),
            BidPrice: GetDecimal(root, "bidPrice", "bid"),
            AskPrice: GetDecimal(root, "askPrice", "ask"),
            Volume24h: GetDecimal(root, "volume", "volume24h"),
            Timestamp: GetTimestamp(root) ?? DateTimeOffset.UtcNow
        );
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Kline>> GetKlinesAsync(
        string symbol,
        string interval,
        int limit,
        CancellationToken ct = default
    )
    {
        var json = await GetAsync(
            $"/api/v1/klines?symbol={symbol}&interval={interval}&limit={limit}",
            ct
        );
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var klines = new List<Kline>();

        foreach (var kline in root.EnumerateArray())
            klines.Add(ParseKline(kline));

        return klines;
    }

    /// <inheritdoc />
    public async Task<Order> PlaceLimitOrderAsync(
        string symbol,
        OrderSide side,
        decimal price,
        decimal quantity,
        string message,
        CancellationToken ct = default
    )
    {
        var order = new Order
        {
            Symbol = symbol,
            Side = side,
            Type = OrderType.Limit,
            Price = price,
            Quantity = quantity,
        };

        var payload = new
        {
            symbol,
            side = side == OrderSide.Buy ? "BUY" : "SELL",
            type = "LIMIT",
            price,
            quantity,
        };

        var json = await PostAsync("/api/v1/order", payload, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        order.ExchangeOrderId = TryGetString(root, "orderId", "id", "ticket") ?? order.Id;
        order.Status = MapStatus(TryGetString(root, "status") ?? "NEW");

        _logger.LogInformation(message, side, quantity, symbol, price, order.ExchangeOrderId);

        return order;
    }

    /// <inheritdoc />
    public async Task<Order> PlaceMarketOrderAsync(
        string symbol,
        OrderSide side,
        decimal quantity,
        CancellationToken ct = default
    )
    {
        var order = new Order
        {
            Symbol = symbol,
            Side = side,
            Type = OrderType.Market,
            Quantity = quantity,
        };

        var payload = new
        {
            symbol,
            side = side == OrderSide.Buy ? "BUY" : "SELL",
            type = "MARKET",
            quantity,
        };

        var json = await PostAsync("/api/v1/order", payload, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        order.ExchangeOrderId = TryGetString(root, "orderId", "id", "ticket") ?? order.Id;
        order.Status = MapStatus(TryGetString(root, "status") ?? "NEW");
        order.Price = GetDecimal(root, "price", "avgPrice", "lastPrice");
        order.FilledQuantity = GetDecimal(root, "executedQty", "filledQuantity", "quantity");
        order.FilledAt = DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "Placed {Side} MARKET {Qty} {Symbol} → OrderId: {Id}",
            side,
            quantity,
            symbol,
            order.ExchangeOrderId
        );

        return order;
    }

    /// <inheritdoc />
    public async Task<bool> CancelOrderAsync(
        string symbol,
        string exchangeOrderId,
        CancellationToken ct = default
    )
    {
        var path = $"/api/v1/order?symbol={symbol}&orderId={exchangeOrderId}";
        await DeleteAsync(path, ct);
        _logger.LogInformation("Cancelled order {OrderId} for {Symbol}", exchangeOrderId, symbol);
        return true;
    }

    /// <inheritdoc />
    public async Task<Order?> GetOrderStatusAsync(
        string symbol,
        string exchangeOrderId,
        CancellationToken ct = default
    )
    {
        var json = await GetAsync($"/api/v1/order?symbol={symbol}&orderId={exchangeOrderId}", ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new Order
        {
            ExchangeOrderId = exchangeOrderId,
            Symbol = symbol,
            Side = TryGetString(root, "side") == "BUY" ? OrderSide.Buy : OrderSide.Sell,
            Price = GetDecimal(root, "price", "avgPrice"),
            Quantity = GetDecimal(root, "origQty", "quantity"),
            FilledQuantity = GetDecimal(root, "executedQty", "filledQuantity"),
            Status = MapStatus(TryGetString(root, "status") ?? "NEW"),
        };
    }

    /// <inheritdoc />
    public async Task<decimal> GetBalanceAsync(string asset, CancellationToken ct = default)
    {
        var json = await GetAsync($"/api/v1/balance?asset={asset}", ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("balances", out var balances))
        {
            foreach (var balance in balances.EnumerateArray())
            {
                if (balance.GetProperty("asset").GetString() == asset)
                    return GetDecimal(balance, "free", "balance");
            }
        }

        return root.TryGetProperty("asset", out var assetNode) && assetNode.GetString() == asset
            ? GetDecimal(root, "free", "balance")
            : 0m;
    }

    private async Task<string> GetAsync(string path, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        return body;
    }

    private async Task<string> PostAsync(string path, object payload, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Post, path);
        request.Content = JsonContent.Create(payload);
        var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        return body;
    }

    private async Task<string> DeleteAsync(string path, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Delete, path);
        var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        return body;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
            request.Headers.Add(ApiKeyHeaderName, _config.ApiKey);
        return request;
    }

    private static Kline ParseKline(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            var items = element.EnumerateArray().ToArray();
            return new Kline(
                OpenTime: DateTimeOffset.FromUnixTimeMilliseconds(items[0].GetInt64()),
                Open: GetDecimal(items[1]),
                High: GetDecimal(items[2]),
                Low: GetDecimal(items[3]),
                Close: GetDecimal(items[4]),
                Volume: GetDecimal(items[5])
            );
        }

        return new Kline(
            OpenTime: GetTimestamp(element) ?? DateTimeOffset.UtcNow,
            Open: GetDecimal(element, "open"),
            High: GetDecimal(element, "high"),
            Low: GetDecimal(element, "low"),
            Close: GetDecimal(element, "close"),
            Volume: GetDecimal(element, "volume")
        );
    }

    private static decimal GetDecimal(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var property))
                continue;

            return GetDecimal(property);
        }

        throw new InvalidOperationException("Required numeric property not found.");
    }

    private static decimal GetDecimal(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var number))
            return number;

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("Required numeric value missing.");

        return decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? GetTimestamp(JsonElement element)
    {
        if (
            !element.TryGetProperty("timestamp", out var timestamp)
            && !element.TryGetProperty("time", out timestamp)
            && !element.TryGetProperty("openTime", out timestamp)
        )
            return null;

        if (
            timestamp.ValueKind == JsonValueKind.Number
            && timestamp.TryGetInt64(out var milliseconds)
        )
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);

        var value = timestamp.GetString();
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed
        )
            ? parsed
            : null;
    }

    private static string? TryGetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.String)
                return property.GetString();

            if (property.ValueKind == JsonValueKind.Number)
                return property.GetRawText();
        }

        return null;
    }

    private static OrderStatus MapStatus(string status) =>
        status switch
        {
            "NEW" or "PLACED" => OrderStatus.New,
            "FILLED" or "EXECUTED" => OrderStatus.Filled,
            "PARTIALLY_FILLED" or "PARTIAL" => OrderStatus.PartiallyFilled,
            "CANCELED" or "CANCELLED" => OrderStatus.Cancelled,
            "REJECTED" => OrderStatus.Rejected,
            _ => OrderStatus.New,
        };

    public Task<Order> PlaceLimitOrderAsync(
        string symbol,
        OrderSide side,
        decimal price,
        decimal quantity,
        CancellationToken ct = default
    )
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Paper trading client - no real orders, simulates fills at current price.
/// Essential for testing strategies before going live.
/// </summary>
public sealed class PaperTradingClient : IExchangeClient
{
    private readonly IExchangeClient _realClient;
    private readonly ILogger<PaperTradingClient> _logger;
    private readonly Dictionary<string, decimal> _balances = new()
    {
        ["USDT"] = 1000m,
        ["ETH"] = 0m,
        ["BTC"] = 0m,
        ["SOL"] = 0m,
    };
    private readonly List<Order> _orders = [];

    public PaperTradingClient(IExchangeClient realClient, ILogger<PaperTradingClient> logger)
    {
        _realClient = realClient;
        _logger = logger;
    }

    public Task<Ticker> GetTickerAsync(string symbol, CancellationToken ct = default) =>
        _realClient.GetTickerAsync(symbol, ct); // Real market data

    public Task<IReadOnlyList<Kline>> GetKlinesAsync(
        string symbol,
        string interval,
        int limit,
        CancellationToken ct = default
    ) => _realClient.GetKlinesAsync(symbol, interval, limit, ct);

    public Task<Order> PlaceLimitOrderAsync(
        string symbol,
        OrderSide side,
        decimal price,
        decimal quantity,
        CancellationToken ct = default
    )
    {
        var order = new Order
        {
            Symbol = symbol,
            Side = side,
            Type = OrderType.Limit,
            Price = price,
            Quantity = quantity,
            ExchangeOrderId = $"PAPER-{Guid.NewGuid():N}"[..16],
            Status = OrderStatus.New,
        };
        _orders.Add(order);
        _logger.LogInformation(
            "[PAPER] Limit {Side} {Qty} {Symbol} @ {Price}",
            side,
            quantity,
            symbol,
            price
        );
        return Task.FromResult(order);
    }

    public Task<Order> PlaceMarketOrderAsync(
        string symbol,
        OrderSide side,
        decimal quantity,
        CancellationToken ct = default
    )
    {
        var order = new Order
        {
            Symbol = symbol,
            Side = side,
            Type = OrderType.Market,
            Quantity = quantity,
            FilledQuantity = quantity,
            ExchangeOrderId = $"PAPER-{Guid.NewGuid():N}"[..16],
            Status = OrderStatus.Filled,
            FilledAt = DateTimeOffset.UtcNow,
        };

        // Simulate balance changes
        var baseAsset = symbol.Replace("USDT", "");
        if (side == OrderSide.Buy)
        {
            _balances["USDT"] -= quantity * order.Price;
            _balances.TryAdd(baseAsset, 0);
            _balances[baseAsset] += quantity;
        }
        else
        {
            _balances.TryAdd(baseAsset, 0);
            _balances[baseAsset] -= quantity;
            _balances["USDT"] += quantity * order.Price;
        }

        _orders.Add(order);
        _logger.LogInformation("[PAPER] Market {Side} {Qty} {Symbol}", side, quantity, symbol);
        return Task.FromResult(order);
    }

    public Task<bool> CancelOrderAsync(
        string symbol,
        string exchangeOrderId,
        CancellationToken ct = default
    )
    {
        var order = _orders.FirstOrDefault(o => o.ExchangeOrderId == exchangeOrderId);
        if (order is not null)
            order.Status = OrderStatus.Cancelled;
        return Task.FromResult(order is not null);
    }

    public Task<Order?> GetOrderStatusAsync(
        string symbol,
        string exchangeOrderId,
        CancellationToken ct = default
    )
    {
        var order = _orders.FirstOrDefault(o => o.ExchangeOrderId == exchangeOrderId);
        return Task.FromResult(order);
    }

    public Task<decimal> GetBalanceAsync(string asset, CancellationToken ct = default)
    {
        _balances.TryGetValue(asset, out var balance);
        return Task.FromResult(balance);
    }

    /// <summary>
    /// Called each tick to simulate limit order fills against current price.
    /// </summary>
    public void SimulateFills(decimal currentPrice)
    {
        foreach (
            var order in _orders.Where(o =>
                o.Status == OrderStatus.New && o.Type == OrderType.Limit
            )
        )
        {
            bool shouldFill =
                order.Side == OrderSide.Buy
                    ? currentPrice <= order.Price
                    : currentPrice >= order.Price;

            if (shouldFill)
            {
                order.Status = OrderStatus.Filled;
                order.FilledQuantity = order.Quantity;
                order.FilledAt = DateTimeOffset.UtcNow;
                order.Price = currentPrice;
                _logger.LogInformation(
                    "[PAPER] Filled {Side} {Qty} @ {Price}",
                    order.Side,
                    order.Quantity,
                    currentPrice
                );
            }
        }
    }
}
