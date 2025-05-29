namespace MigratedDatabaseServices.Data;

public class TransactionData
{
    public Guid TransactionId { get; set; }
    public Guid BuyerId { get; set; }
    public Guid SellerId { get; set; }
    public required string InstrumentId { get; set; }
    public int Size { get; set; }
    public decimal Price { get; set; }
    public decimal SpreadPrice { get; set; }
    public DateTime Time { get; set; } //Maybe string is better? depends on implementation i guess. Left as DateTime object for now
    public bool Succeeded { get; set; }
        
    public string BrokerName { get; set; }

}