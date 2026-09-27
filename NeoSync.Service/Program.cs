using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NeoSync.Service;

await Host.CreateDefaultBuilder(args)
    .UseWindowsService(options => options.ServiceName = "NeoSyncHelper")
    .ConfigureServices(services => services.AddHostedService<NeoSyncWorker>())
    .Build()
    .RunAsync();
