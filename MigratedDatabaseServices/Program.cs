using Microsoft.Extensions.Hosting;
using MigratedDatabaseServices;

var builder = Host.CreateDefaultBuilder(args);

builder.ConfigureServices((context, services) =>
{
    services.SetupNats(context.Configuration);
    services.SetupBook(context.Configuration);
});

var host = builder.Build();

await host.RunAsync();