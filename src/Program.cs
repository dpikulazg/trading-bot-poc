using CryptoTradingBot.Configuration;
using CryptoTradingBot.Exchange;
using CryptoTradingBot.Services;
using CryptoTradingBot.Strategies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services
	.AddOptions<BotConfiguration>()
	.Bind(builder.Configuration.GetSection(BotConfiguration.SectionName))
	.ValidateOnStart();

builder.Services.AddHttpClient<BinanceClient>();
builder.Services.AddHttpClient<MetaTrader5Client>();

builder.Services.AddSingleton<RiskManager>();

builder.Services.AddSingleton<IExchangeClient>(serviceProvider =>
{
	var configuration = serviceProvider
		.GetRequiredService<Microsoft.Extensions.Options.IOptions<BotConfiguration>>()
		.Value;

	IExchangeClient realClient = configuration.Exchange.Name.ToUpperInvariant() switch
	{
		"BINANCE" => serviceProvider.GetRequiredService<BinanceClient>(),
		"METATRADER5" or "MT5" => serviceProvider.GetRequiredService<MetaTrader5Client>(),
		var name => throw new InvalidOperationException(
			$"Unsupported exchange '{name}'. Configure Binance or MetaTrader5."
		),
	};

	return configuration.Risk.PaperTrading
		? new PaperTradingClient(
			realClient,
			serviceProvider.GetRequiredService<ILogger<PaperTradingClient>>()
		)
		: realClient;
});

builder.Services.AddSingleton<GridStrategy>();
builder.Services.AddSingleton<DcaStrategy>();
builder.Services.AddSingleton<ITradingStrategy>(serviceProvider =>
{
	var configuration = serviceProvider
		.GetRequiredService<Microsoft.Extensions.Options.IOptions<BotConfiguration>>()
		.Value;

	return configuration.Trading.Strategy.ToUpperInvariant() switch
	{
		"GRID" => serviceProvider.GetRequiredService<GridStrategy>(),
		"DCA" => serviceProvider.GetRequiredService<DcaStrategy>(),
		var strategy => throw new InvalidOperationException(
			$"Unsupported strategy '{strategy}'. Configure Grid or DCA."
		),
	};
});

builder.Services.AddHostedService<TradingBotService>();

await builder.Build().RunAsync();
