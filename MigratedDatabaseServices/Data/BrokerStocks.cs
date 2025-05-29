namespace MigratedDatabaseServices.Data;

public class BrokerStocks
{
    public const string SectionName = "BrokerStocks";
    public HashSet<string> JPMorgan { get; set; } = [];
    public HashSet<string> NASDAQ { get; set; } = [];
    public HashSet<string> Nordea { get; set; } = [];
}