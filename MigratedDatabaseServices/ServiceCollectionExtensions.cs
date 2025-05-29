using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MigratedDatabaseServices.Data;
using NATS.Net;

namespace MigratedDatabaseServices;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection SetupNats(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<NatsOptions>(configuration.GetSection(NatsOptions.SectionName));

        services.AddSingleton<NatsClient>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<NatsOptions>>().Value;
            return new NatsClient(options.NatsUrl);
        });
        return services;
    }
    
    public static IServiceCollection SetupBook(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<InstrumentsOptions>(configuration.GetSection(InstrumentsOptions.SectionName));
        services.AddSingleton<IBook, Book>();
        services.AddSingleton<IDBHandler, DBHandler>();
        services.AddHostedService<Logic>();
        return services;
    }
}