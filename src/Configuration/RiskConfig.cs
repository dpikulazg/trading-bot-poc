namespace CryptoTradingBot.Configuration
{
    //public sealed class TradingConfig
    //{
    //    public string Symbol { get; set; } = "ETHUSDT";
    //    public string Strategy { get; set; } = "Grid"; // Grid | DCA
    //    public decimal OrderSizeUsdt { get; set; } = 10m;
    //    public int PollingIntervalMs { get; set; } = 5000;

    //    // Grid strategy
    //    public decimal GridUpperPrice { get; set; } = 4000m;
    //    public decimal GridLowerPrice { get; set; } = 3000m;
    //    public int GridLevels { get; set; } = 10;

    //    // DCA strategy
    //    public decimal DcaBuyDropPercent { get; set; } = 2.0m;
    //    public decimal DcaTakeProfitPercent { get; set; } = 3.0m;
    //    public int DcaMaxOrders { get; set; } = 5;
    //    public decimal DcaMultiplier { get; set; } = 1.5m;
    //}

    public sealed class RiskConfig
    {
        public decimal MaxDailyLossUsdt { get; set; } = 50m;
        public decimal MaxPositionUsdt { get; set; } = 500m;
        public decimal StopLossPercent { get; set; } = 5.0m;
        public bool PaperTrading { get; set; } = true;
        public int CooldownAfterLossMinutes { get; set; } = 30;
    }
}
