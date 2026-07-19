global using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Coflnet.Core;
using Coflnet.Security.OpenBao;

namespace Coflnet.Payments
{
    public class Program
    {
        /// <summary>
        /// Entrypoint
        /// </summary>
        /// <param name="args"></param>
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        /// <summary>
        /// Creates the host builder 
        /// </summary>
        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((_, config) => config.AddOpenBaoFromEnvironment())
                .ConfigureLogging((context, logging) =>
                {
                    // Shared OTel logging configuration from Coflnet.Core.
                    // Bridges ILogger -> OTLP (HttpProtobuf) with trace-log correlation,
                    // k8s pod attributes, and DEV_LOGGING console fallback.
                    logging.AddOpenTelemetryLogging(
                        context.Configuration,
                        context.Configuration["JAEGER_SERVICE_NAME"] ?? "payments");
                })
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });
    }
}
