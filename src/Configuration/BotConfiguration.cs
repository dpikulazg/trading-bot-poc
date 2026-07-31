namespace CryptoTradingBot.Configuration
{
    /// <summary>
    /// Root configuration for the trading bot.
    /// </summary>
    public sealed class BotConfiguration
    {
        public const string SectionName = "TradingBot";

        public ExchangeConfig Exchange { get; set; } = new();
        public TradingConfig Trading { get; set; } = new();
        public RiskConfig Risk { get; set; } = new();
    }

    public sealed class ExchangeConfig
    {
        /// <summary>Binance, Bybit, etc.</summary>
        public string Name { get; set; } = "Binance";
        public string ApiKey { get; set; } = string.Empty;
        public string ApiSecret { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = "https://api.binance.com";
        public bool UseSandbox { get; set; } = true;
    }
}
