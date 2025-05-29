namespace MigratedDatabaseServices.Data;

public class InstrumentsOptions
{
    public const string SectionName = "TradingOptions";
    public HashSet<string> Stocks { get; set; } = [];
}