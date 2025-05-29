using Microsoft.Extensions.Logging;
using MigratedDatabaseServices.Data;
using NATS.Client.JetStream.Models;
using NATS.Net;

namespace MigratedDatabaseServices;

public interface IBook
{
    public void Start();
    public void Stop();
}

public class Book : IBook
{
    private readonly ILogger<Book> _logger;
    private readonly IDBHandler _dbHandler;
    private const string DanskeBankClientName = "Danske_Bank";
    
    private readonly NatsClient _natsClient;
    private readonly Dictionary<string, CancellationTokenSource> _subscriptionTokens = new();
    private readonly List<Task> _subscriptionTasks = new();

    public Book(ILogger<Book> logger, IDBHandler dBHandler, NatsClient natsClient)
    {
        _logger = logger;
        _dbHandler = dBHandler;
        _natsClient = natsClient;
    }
    
    public void Start()
    {
        CreateConsumers();
    }

    private async void CreateConsumers()
    {
        var consumerBookConfig = new ConsumerConfig
        {
            Name = "bookOrderConsumer",
            DurableName = "bookOrderConsumer",
            DeliverPolicy = ConsumerConfigDeliverPolicy.All,
            DeliverGroup = "Book",
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            FilterSubject = TopicGenerator.TopicForBookingOrder()
        };
        var consumerHedgeConfig = new ConsumerConfig
        {
            Name = "hedgeOrderConsumer",
            DurableName = "hedgeOrderConsumer",
            DeliverPolicy = ConsumerConfigDeliverPolicy.All,
            DeliverGroup = "Book",
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            FilterSubject = TopicGenerator.TopicForHedgingOrder()
        };
        
        var stream = "streamOrders";
        
        await _natsClient.CreateJetStreamContext().CreateOrUpdateConsumerAsync(stream, consumerBookConfig);
        await _natsClient.CreateJetStreamContext().CreateOrUpdateConsumerAsync(stream, consumerHedgeConfig);
        PublishClients();
        SubscribeToBookings();
        SubscribeToHedgings();
    }
    
    private async void PublishClients()
    {
        var clients = _dbHandler.GetAllClients();
        var topic = TopicGenerator.TopicForAllClients();
        await _natsClient.PublishAsync(topic, clients);
    }
    private void SubscribeToBookings()
    {
        var topicBookOrder = TopicGenerator.TopicForBookingOrder();
        if (_subscriptionTokens.TryGetValue(topicBookOrder, out var existingCts))
        {
            existingCts.Cancel();
            _subscriptionTokens.Remove(topicBookOrder);
        }

        var cts = new CancellationTokenSource();
        _subscriptionTokens[topicBookOrder] = cts;
        
        var task = Task.Run(async () =>
        {
            try
            {
                var consumer = await _natsClient.CreateJetStreamContext()
                    .GetConsumerAsync("streamOrders", "bookOrderConsumer", cts.Token);
                await foreach (var msg in consumer.ConsumeAsync<TransactionData>(cancellationToken: cts.Token))
                {
                    if (msg.Data == null)
                    {
                        _logger.LogError("BookOrder consumer returned null");
                        await msg.AckAsync(cancellationToken: cts.Token);
                        return;
                    }

                    //await msg.AckProgressAsync(); TODO figure out time
                    BookOrder(msg.Data);
                    await msg.AckAsync(cancellationToken: cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogError("BookOrder consumer cancelled");
            }
        }, cts.Token);
        _subscriptionTasks.Add(task);
    }
    private void SubscribeToHedgings()
    {
        var topicHedgeOrder = TopicGenerator.TopicForHedgingOrder();
        if (_subscriptionTokens.TryGetValue(topicHedgeOrder, out var existingCts))
        {
            existingCts.Cancel();
            _subscriptionTokens.Remove(topicHedgeOrder);
        }

        var cts = new CancellationTokenSource();
        _subscriptionTokens[topicHedgeOrder] = cts;
        
        var task = Task.Run(async () =>
        {
            try
            {
                var consumer = await _natsClient.CreateJetStreamContext()
                    .GetConsumerAsync("streamOrders", "hedgeOrderConsumer", cts.Token);
                await foreach (var msg in consumer.ConsumeAsync<TransactionData>(cancellationToken: cts.Token))
                {
                    //await msg.AckProgressAsync(); TODO figure out time
                    if (msg.Data == null)
                    {
                        _logger.LogError("HedgeOrder consumer returned null");
                        await msg.AckAsync(cancellationToken: cts.Token);
                        return;
                    }

                    _logger.LogInformation("Book hedge order received");
                    HedgeOrder(msg.Data);
                    await msg.AckAsync(cancellationToken: cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Book hedge order consumer cancelled");
            }
        }, cts.Token);
        _subscriptionTasks.Add(task);
    }

    public void BookOrder(TransactionData transaction)
    {
        var danskeBankId = _dbHandler.GetClientGuid(DanskeBankClientName);
        if (transaction.BuyerId == Guid.Empty)
        {
            //Customer is selling
            transaction.BuyerId = danskeBankId;
            transaction.SpreadPrice = -transaction.SpreadPrice;
        }
        else
        {
            //Customer is buying
            transaction.SellerId = danskeBankId;
        }
        _logger.LogInformation("Book order called for {InstrumentId}", transaction.InstrumentId);
        _dbHandler.AddTransaction(transaction);
    }

    public void HedgeOrder(TransactionData response)
    {
        _logger.LogInformation("Hedge order called for {InstrumentId}", response.InstrumentId);
        var danskeBankId = _dbHandler.GetClientGuid(DanskeBankClientName);
        var brokerId = _dbHandler.GetClientGuid(response.BrokerName);

        var trans1 = new TransactionData
        {
            TransactionId = response.TransactionId,
            BuyerId = response.BuyerId,
            SellerId = response.SellerId,
            InstrumentId = response.InstrumentId,
            Size = response.Size,
            Price = response.Price,
            SpreadPrice = response.SpreadPrice,
            Time = response.Time,
            Succeeded = response.Succeeded
        };
        var trans2 = new TransactionData
        {
            TransactionId = response.TransactionId,
            BuyerId = response.BuyerId,
            SellerId = response.SellerId,
            InstrumentId = response.InstrumentId,
            Size = response.Size,
            Price = response.Price,
            SpreadPrice = 0.0m,
            Time = response.Time,
            Succeeded = response.Succeeded
        };
        if (response.BuyerId == Guid.Empty)
        {
            //Client is selling stock
            trans1.SpreadPrice = -trans1.SpreadPrice;
            trans1.BuyerId = danskeBankId;
            trans2.SellerId = danskeBankId;
            trans2.BuyerId = brokerId;
            
        }else
        {
            //Client is buying stock
            trans1.SellerId = danskeBankId;
            trans2.SellerId = brokerId;
            trans2.BuyerId = danskeBankId;
        }
        _dbHandler.AddTransaction(trans1);
        _dbHandler.AddTransaction(trans2);
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
    }
}