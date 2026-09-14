namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>Which way an order goes. Venue wire values differ (ProjectX sends 0 bid / 1 ask); adapters map.</summary>
public enum OrderSide
{
    /// <summary>Buy / long.</summary>
    Buy,

    /// <summary>Sell / short.</summary>
    Sell,
}
