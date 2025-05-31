using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MigratedDatabaseServices.Data;
using NATS.Client.JetStream.Models;
using NATS.Net;

namespace MigratedDatabaseServices;

using Tier = Data.Tier;

public interface IDBHandler
{
    public void Start();
    public void Stop();
    public void AddClient(string name);
    public void AddClientCustomer(string name, string username, string password, Tier tier);
    public void AddTransaction(TransactionData transaction);
    public Tier GetClientTier(string name);
    public decimal GetClientBalance(string name);
    public Guid GetClientGuid(string name);
    public List<TransactionData> GetClientTransactions(string name);
    public List<HoldingData> GetClientHoldings(Guid clientId);
    public List<HoldingData> GetAllHoldings();

    public List<ClientData> GetAllClients();

    public void ResetDB();
    
    public List<TargetPosition> GetTargetPositions();
}

public class DBHandler : IDBHandler
{
    private string databaseFilePath = "./Data/QuoteUnquoteDB.json";
    
    private BrokerStocks _brokerStocks;
    private readonly ILogger<DBHandler> _logger;
    private InstrumentsOptions _instrumentsOptions;
    private Lock _readerLock = new();
    
    private readonly NatsClient _natsClient;
    private readonly Dictionary<string, CancellationTokenSource> _subscriptionTokens = new();
    private readonly List<Task> _subscriptionTasks = new();

    public DBHandler(IOptions<BrokerStocks> brokerStocks
        , ILogger<DBHandler> logger
        , IOptions<InstrumentsOptions> tradingOptions
        , NatsClient natsClient)
    {
        _brokerStocks = brokerStocks.Value;
        _logger = logger;
        _instrumentsOptions = tradingOptions.Value;
        _natsClient = natsClient;
    }

    public void Start()
    {
        ResetDB();
        SetupConsumers();
    }

    private async void SetupConsumers()
    {
        var consumerConfig = new ConsumerConfig
        {
            Name = "loginRequestConsumer",
            DurableName = "loginRequestConsumer",
            DeliverPolicy = ConsumerConfigDeliverPolicy.All,
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            FilterSubject = TopicGenerator.TopicForLoginRequest()
        };
        
        var stream = "streamLoginRequest";
        
        await _natsClient.CreateJetStreamContext().CreateOrUpdateConsumerAsync(stream, consumerConfig);
        SubscribeToLogin();
        PublishAllClients();
        SetupTargetPositions();
    }

    private void SubscribeToLogin()
    {
        _logger.LogInformation("Subscribing to login request");
        var topic = TopicGenerator.TopicForLoginRequest();

        if (_subscriptionTokens.TryGetValue(topic, out var existingCts))
        {
            existingCts.Cancel();
            _subscriptionTokens.Remove(topic);
        }

        var cts = new CancellationTokenSource();
        _subscriptionTokens[topic] = cts;
        
        var task = Task.Run(async () =>
        {
            try
            {
                var consumer = await _natsClient.CreateJetStreamContext()
                    .GetConsumerAsync("streamLoginRequest", "loginRequestConsumer", cts.Token);
                await foreach (var msg in consumer.ConsumeAsync<LoginInfo>(cancellationToken: cts.Token))
                {
                    if (msg.Data == null)
                    {
                        _logger.LogError("LoginRequest consumer returned null");
                        await msg.AckAsync(cancellationToken: cts.Token);
                        return;
                    }
                    //await msg.AckProgressAsync(); TODO figure out time
                    CheckLogin(msg.Data);
                    await msg.AckAsync(cancellationToken: cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Login request was cancelled or at least consumer wasn't there");
            }
        }, cts.Token);
        _subscriptionTasks.Add(task);
    }
    private async void PublishAllClients()
    {
        var allClients = GetAllClients();
        foreach (var client in allClients)
        {
            var topicClient = TopicGenerator.TopicForDBDataOfClient(client.ClientId.ToString());
            client.Holdings = GetClientHoldings(client.ClientId);
            await _natsClient.PublishAsync(topicClient, client);
        }
    }
    private async void SetupTargetPositions()
    {
        var topicAllTargetPositions = TopicGenerator.TopicForAllTargetPositions();
        var allTargetPositions = GetTargetPositions();
        foreach (var target in allTargetPositions)
        {
            var topic = TopicGenerator.TopicForTargetPositionUpdate(target.InstrumentId);
            if (_subscriptionTokens.TryGetValue(topic, out var existingCts))
            {
                existingCts.Cancel();
                _subscriptionTokens.Remove(topic);
            }
        
            var cts = new CancellationTokenSource();
            _subscriptionTokens[topic] = cts;
            var consumerConfig = new ConsumerConfig
            {
                Name = "targetPositionConsumer" + target.InstrumentId,
                DurableName = "targetPositionConsumer" + target.InstrumentId,
                DeliverPolicy = ConsumerConfigDeliverPolicy.All,
                DeliverGroup = "DBHandler",
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                FilterSubject = topic
            };
            
            var stream = "StreamDBData";
            
            var consumer = await _natsClient.CreateJetStreamContext().CreateOrUpdateConsumerAsync(stream, consumerConfig, cts.Token);
            var task = Task.Run(async () =>
            {
                try
                {
                    await foreach (var natsMsg in consumer.ConsumeAsync<TargetPosition>(cancellationToken: cts.Token))
                    {
                        var targetPosition = natsMsg.Data;
                        if (targetPosition != null)
                            UpdateTargetPosition(targetPosition);
                        await natsMsg.AckAsync(cancellationToken: cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Stopped subscription: {Topic}", topic);
                }
            }, cts.Token);
            _subscriptionTasks.Add(task);
            
        }
        await _natsClient.PublishAsync(topicAllTargetPositions, allTargetPositions);
    }
    
    private void UpdateTargetPosition(TargetPosition newTarget)
    {
        var db = DeserializeDB();

        var targetPosition = db.TargetPositions.Find(t => t.InstrumentId == newTarget.InstrumentId);
        if (targetPosition != null)
        {
            targetPosition.Target = newTarget.Target;
        }
        else
        {
            db.TargetPositions.Add(newTarget);
        }
        
        Serialize(db);
    }

    public async void Stop()
    {
        var keys = _subscriptionTokens.Keys.ToList(); 

        foreach (var key in keys)
        {
            if (_subscriptionTokens.TryGetValue(key, out var cts))
            {
                await cts.CancelAsync();
            }
        }

        _subscriptionTokens.Clear();
        await Task.WhenAll(_subscriptionTasks);

    }

    public void AddClient(string name)
    {
        DatabaseData db = DeserializeDB();

        if(db.Clients.Exists(x => x.Name.Equals(name)))
        {
            return;
        }

        ClientData client = new ClientData
        {
            ClientId = Guid.NewGuid(),
            Name = name,
            Balance = 1000.0m,
            Tier = Tier.Premium,
            Holdings = new List<HoldingData>(),
        };

        db.Clients.Add(client);
        Serialize(db);
        return;
    }

    //If you are a customer, you are also automatically a client. This relation does not go the other way.
    //For instance, brokers are clients, but do not need username/password for our system.
    public void AddClientCustomer(string name, string username, string password, Tier tier)
    {
        DatabaseData db = DeserializeDB();

        Guid ID = Guid.NewGuid();

        CustomerData customer = new CustomerData
        {
            ClientId = ID,
            Username = username,
            Password = hashPassword(password, ID, db)
        };
        db.Customers.Add(customer);

        ClientData client = new ClientData
        {
            ClientId = ID,
            Name = name,
            Balance = 1000.0m,
            Tier = tier,
            Holdings = new List<HoldingData>(),
        };
        db.Clients.Add(client);

        Serialize(db);
        return;
    }

    public async void AddTransaction(TransactionData transaction)
    {
        DatabaseData db = DeserializeDB();

        transaction.TransactionId = Guid.NewGuid();
        transaction.Time = DateTime.Now;
        db.Transactions.Add(transaction);
        if(transaction.Succeeded)
        {
            db = UpdateHoldings(db, transaction);
            var buyer = db.Clients.Find(x => x.ClientId == transaction.BuyerId);
            if(buyer != null)
            {
                db.Clients.Remove(buyer);
                buyer.Balance -= transaction.Size * (transaction.Price + transaction.SpreadPrice);
                db.Clients.Add(buyer);
                Serialize(db);
                var topic = TopicGenerator.TopicForDBDataOfClient(buyer.ClientId.ToString());
                buyer.Holdings = GetClientHoldings(buyer.ClientId);
                await _natsClient.PublishAsync(topic, buyer);
            }
            var seller = db.Clients.Find(x => x.ClientId == transaction.SellerId);
            if (seller != null)
            {
                db.Clients.Remove(seller);
                seller.Balance += transaction.Size * (transaction.Price + transaction.SpreadPrice);
                db.Clients.Add(seller);
                Serialize(db);
                var topic = TopicGenerator.TopicForDBDataOfClient(seller.ClientId.ToString());
                seller.Holdings = GetClientHoldings(seller.ClientId);
                await _natsClient.PublishAsync(topic, seller);
            }
        }
        Serialize(db);
        return;
    }

    private DatabaseData UpdateHoldings(DatabaseData db, TransactionData trans)
    {
        var currentHoldBuyer = db.Holdings.Find(x => x.ClientId == trans.BuyerId && x.InstrumentId == trans.InstrumentId);
        if(currentHoldBuyer == null)
        {
            HoldingData newHolding = new HoldingData
            {
                ClientId = trans.BuyerId,
                InstrumentId = trans.InstrumentId,
                Size = trans.Size
            };
            db.Holdings.Add(newHolding);
        }
        else
        {
            db.Holdings.Remove(currentHoldBuyer);
            currentHoldBuyer.Size += trans.Size;
            db.Holdings.Add(currentHoldBuyer);
        }
        var currentHoldSeller = db.Holdings.Find(x => x.ClientId == trans.SellerId && x.InstrumentId == trans.InstrumentId);
        if (currentHoldSeller == null)
        {
            HoldingData newHolding = new HoldingData
            {
                ClientId = trans.SellerId,
                InstrumentId = trans.InstrumentId,
                Size = -trans.Size
            };
            db.Holdings.Add(newHolding);
        }
        else
        {
            db.Holdings.Remove(currentHoldSeller);
            currentHoldSeller.Size -= trans.Size;

            if(currentHoldSeller.Size > 0)
            {
                db.Holdings.Add(currentHoldSeller);
            }
        }
        return db;
    }

    public Tier GetClientTier(string name)
    {
        DatabaseData db = DeserializeDB();
        var client = db.Clients.Find(x => x.Name.Equals(name));
        if(client == null)
        {
            return Tier.ClientNotFound;
        }
        return client.Tier;
    }

    public decimal GetClientBalance(string name)
    {
        DatabaseData db = DeserializeDB();
        var client = db.Clients.Find(x => x.Name.Equals(name));
        if (client == null)
        {
            return -1.0m;
        }
        return client.Balance;
    }

    public Guid GetClientGuid(string name)
    {
        DatabaseData db = DeserializeDB();
        var client = db.Clients.Find(x => x.Name.Equals(name));
        if (client == null)
        {
            return Guid.Empty;
        }
        return client.ClientId;
    }

    public List<TransactionData> GetClientTransactions(string name)
    {
        DatabaseData db = DeserializeDB();
        var client = db.Clients.Find(x => x.Name.Equals(name));
        if (client == null)
        {
            return new List<TransactionData>();
        }
        List<TransactionData> transBuyer = db.Transactions.FindAll(x => x.BuyerId == client.ClientId);
        List<TransactionData> transSeller = db.Transactions.FindAll(x => x.SellerId == client.ClientId);
        return transBuyer.Concat(transSeller).ToList();
    }

    public List<HoldingData> GetClientHoldings(Guid clientId)
    {
        DatabaseData db = DeserializeDB();
        List<HoldingData> holdings = db.Holdings.FindAll(x => x.ClientId == clientId);
        return holdings;
    }

    public List<HoldingData> GetAllHoldings()
    {
        DatabaseData db = DeserializeDB();
        return db.Holdings;
    }

    public List<ClientData> GetAllClients()
    {
        DatabaseData db = DeserializeDB();
        return db.Clients;
    }

    private async void CheckLogin(LoginInfo info)
    {
        var db = DeserializeDB();

        var username = info.Username;
        var password = info.Password;

        var customer = db.Customers.Find(x => x.Username.Equals(username) && x.Password.Equals(hashPassword(password,x.ClientId, db)));
        info.IsAuthenticated = customer != null;
        info.ClientId = customer == null ?  Guid.Empty : customer.ClientId;
        // info.Username = "-"; //TODO Implement!
        // info.Password = "-";

        var topic = TopicGenerator.TopicForLoginResponse();
        //_observable.Publish(topic, info, isTransient: true);
        await _natsClient.PublishAsync(topic, info);
        return;
    }

    public void ResetDB()
    {
        DatabaseData db = new DatabaseData
        {
            Clients = new List<ClientData>(),
            Customers = new List<CustomerData>(),
            Transactions = new List<TransactionData>(),
            Holdings = new List<HoldingData>(),
            Salts = new List<SaltData>(),
            TargetPositions = new List<TargetPosition>()
        };

        var initialHoldingSize = 100;
        var initialBrokerBalance = 1000000.0m;
        var externalBrokerTier = Tier.External;

        var danskeBankGuid = Guid.NewGuid();
        db.Clients.Add(new ClientData
        {
            ClientId = danskeBankGuid,
            Name = "Danske_Bank",
            Balance = initialBrokerBalance,
            Tier = Tier.Internal,
        });
        foreach (string s in _instrumentsOptions.Stocks)
        {
            db.Holdings.Add(new HoldingData
            {
                ClientId = danskeBankGuid,
                InstrumentId = s,
                Size = initialHoldingSize
            });
            db.TargetPositions.Add(new TargetPosition
            {
                InstrumentId = s,
                Target = 97
            });
        }

        var nordeaGuid = Guid.NewGuid();
        db.Clients.Add(new ClientData
        {
            ClientId = nordeaGuid,
            Name = "Nordea",
            Balance = initialBrokerBalance,
            Tier = externalBrokerTier
        });
        foreach (string s in _brokerStocks.Nordea)
        {
            db.Holdings.Add(new HoldingData
            {
                ClientId = nordeaGuid,
                InstrumentId = s,
                Size = initialHoldingSize
            });
        }

        var NASDAQGuid = Guid.NewGuid();
        db.Clients.Add(new ClientData
        {
            ClientId = NASDAQGuid,
            Name = "NASDAQ",
            Balance = initialBrokerBalance,
            Tier = externalBrokerTier
        });
        foreach (string s in _brokerStocks.NASDAQ)
        {
            db.Holdings.Add(new HoldingData
            {
                ClientId = NASDAQGuid,
                InstrumentId = s,
                Size = initialHoldingSize
            });
        }

        var JPMorganGuid = Guid.NewGuid();
        db.Clients.Add(new ClientData
        {
            ClientId = JPMorganGuid,
            Name = "JPMorgan",
            Balance = initialBrokerBalance,
            Tier = externalBrokerTier
        });
        foreach (string s in _brokerStocks.JPMorgan)
        {
            db.Holdings.Add(new HoldingData
            {
                ClientId = JPMorganGuid,
                InstrumentId = s,
                Size = initialHoldingSize
            });
        }

        Serialize(db);

        AddClientCustomer("Anders", "KP", "KP", Tier.External);
        AddClientCustomer("Anders2", "KP2", "KP", Tier.Internal);
        AddClientCustomer("Mathias", "Dyberg", "Dyberg", Tier.Premium);
        return;
    }

    public List<TargetPosition> GetTargetPositions()
    {
        var db = DeserializeDB();
        return db.TargetPositions;
    }

    private void Serialize(DatabaseData db)
    {
        string jsonString = JsonSerializer.Serialize(db);
        File.WriteAllText(databaseFilePath, jsonString);
        return;
    }

    private DatabaseData DeserializeDB()
    {
        lock(_readerLock)
        {
            string jsonString = File.ReadAllText(databaseFilePath);
            return JsonSerializer.Deserialize<DatabaseData>(jsonString)!;
        }
    }

    private string hashPassword(string password, Guid clientId, DatabaseData db)
    {
        var salt = db.Salts.Find(x => x.ClientId == clientId);
        if (salt == null)
        {
            salt = new SaltData
            {
                ClientId = clientId,
                Salt = System.Text.Encoding.Default.GetString(RandomNumberGenerator.GetBytes(16))
            };
            db.Salts.Add(salt);
        }
        string saltedPassword = salt.Salt + password;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(saltedPassword));
        return System.Text.Encoding.Default.GetString(hash);
    }
}