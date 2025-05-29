using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MigratedDatabaseServices;

public class Logic : IHostedService
{
    private readonly ILogger<Logic> _logger;
    private readonly IBook _book;
    private readonly IDBHandler _dbHandler;

    public Logic(ILogger<Logic> logger, IBook book, IDBHandler dbHandler)
    {
        _logger = logger;
        _book = book;
        _dbHandler = dbHandler;
    }
    
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.ServiceStartingUp(nameof(Book));
        
        _dbHandler.Start();
        _book.Start();
        
        _logger.ServiceStarted(nameof(Book));
        
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.ServiceStopped(nameof(Book));
        
        _dbHandler.Stop();
        _book.Stop();
        
        _logger.ServiceStopped(nameof(Book));
        
        return Task.CompletedTask;
    }

}