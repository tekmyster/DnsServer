using DnsServerCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]))
    throw new ArgumentException("Provide the absolute configuration directory.");
string configuration = args[0];
await Host.CreateDefaultBuilder()
    .UseWindowsService(options => options.ServiceName = "TectaniumDDI")
    .ConfigureServices(services =>
    {
        // No automatic firewall or host DNS changes. Keep configuration outside the binaries.
        services.AddSingleton(_ => new DnsWebService(false, configuration));
        services.AddHostedService<TectaniumWorker>();
    }).Build().RunAsync();

sealed class TectaniumWorker(DnsWebService service) : BackgroundService
{
    public override async Task StartAsync(CancellationToken token)
    {
        await service.StartAsync(true);
        await base.StartAsync(token);
    }
    protected override Task ExecuteAsync(CancellationToken token) => Task.Delay(Timeout.Infinite, token);
    public override async Task StopAsync(CancellationToken token)
    {
        await service.StopAsync();
        await base.StopAsync(token);
    }
}
