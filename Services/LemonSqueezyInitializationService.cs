using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

/// <summary>
/// Background service that initializes LemonSqueezy variant discovery on application startup
/// </summary>
public class LemonSqueezyInitializationService : IHostedService
{
    private readonly IServiceProvider serviceProvider;
    private readonly ILogger<LemonSqueezyInitializationService> logger;

    public LemonSqueezyInitializationService(
        IServiceProvider serviceProvider,
        ILogger<LemonSqueezyInitializationService> logger)
    {
        this.serviceProvider = serviceProvider;
        this.logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting LemonSqueezy variant discovery...");
        
        try
        {
            using var scope = serviceProvider.CreateScope();
            var lemonSqueezyService = scope.ServiceProvider.GetRequiredService<LemonSqueezyService>();
            await lemonSqueezyService.DiscoverVariantsAsync();
            logger.LogInformation("LemonSqueezy variant discovery completed successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to discover LemonSqueezy variants during startup");
            // Don't fail startup if variant discovery fails - fallback to config values
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
