using Microsoft.Extensions.Logging;

namespace MigratedDatabaseServices;

public static partial class LoggerExtensions
{
    [LoggerMessage(LogLevel.Information, "Service {ServiceName} starting up...")]
    public static partial void ServiceStartingUp(this ILogger logger, string serviceName);
    
    [LoggerMessage(LogLevel.Information, "Service {ServiceName} started")]
    public static partial void ServiceStarted(this ILogger logger, string serviceName);
    
    [LoggerMessage(LogLevel.Information, "Service {ServiceName} stopping...")]
    public static partial void ServiceStopping(this ILogger logger, string serviceName);
    
    [LoggerMessage(LogLevel.Information, "Service {ServiceName} stopped")]
    public static partial void ServiceStopped(this ILogger logger, string serviceName);
}