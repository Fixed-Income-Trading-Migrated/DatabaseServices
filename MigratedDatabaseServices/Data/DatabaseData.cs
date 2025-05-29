namespace MigratedDatabaseServices.Data;

public class DatabaseData
{
    public List<ClientData> Clients { get; set; }
    public List<CustomerData> Customers { get; set; }
    public List<HoldingData> Holdings { get; set; }
    public List<TransactionData> Transactions { get; set; }
    public List<SaltData> Salts { get; set; }
    public List<TargetPosition> TargetPositions { get; set; }
}