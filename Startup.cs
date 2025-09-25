using System;
using System.IO;
using System.Reflection;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;
using Stripe;
using Prometheus;
using System.Net;
using Microsoft.AspNetCore.Diagnostics;
using Newtonsoft.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using hypixel;
using Newtonsoft.Json.Converters;

namespace Coflnet.Payments
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }
        Counter errorCount = Metrics.CreateCounter("payments_error", "Counts the amount of error responses handed out");
        Counter badRequestCount = Metrics.CreateCounter("payments_bad_request", "Counts the responses for invalid requests");

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers().AddNewtonsoftJson(json =>
            {
                json.SerializerSettings.Converters.Add(new StringEnumConverter());
                json.SerializerSettings.DateTimeZoneHandling = DateTimeZoneHandling.Utc;
            });
            services.AddSwaggerGenNewtonsoftSupport().AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "Payments", Version = "0.0.1", License = new OpenApiLicense { Name = "MIT" } });
                // Set the comments path for the Swagger JSON and UI.
                var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
                var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
                c.IncludeXmlComments(xmlPath);
            });
            if (Configuration["DB_CONNECTION"].StartsWith("server"))
            {
                var serverVersion = new MariaDbServerVersion(new Version(Configuration["MARIADB_VERSION"]));
                services.AddDbContext<PaymentContext>(
                    dbContextOptions => dbContextOptions
                        .UseMySql(Configuration["DB_CONNECTION"], serverVersion)
                );
            }
            else
                services.AddDbContext<PaymentContext>(
                    dbContextOptions => dbContextOptions
                        .UseNpgsql(Configuration["DB_CONNECTION"])
                        .EnableSensitiveDataLogging() // <-- These two calls are optional but help
                        .EnableDetailedErrors()       // <-- with debugging (remove for production).
                );
            if(Configuration["OLD_DB_CONNECTION"] != null)
            {
                var serverVersion = new MariaDbServerVersion(new Version(Configuration["MARIADB_VERSION"]));
                Console.WriteLine("Registering old database\n--------------");
                services.AddDbContext<OldPaymentContext>(
                    dbContextOptions => dbContextOptions
                        .UseMySql(Configuration["OLD_DB_CONNECTION"], serverVersion)
                        .EnableSensitiveDataLogging() // <-- These two calls are optional but help
                        .EnableDetailedErrors()       // <-- with debugging (remove for production).
                );
            }
            services.AddScoped<TransactionService>();
            services.AddScoped<UserService>();
            services.AddScoped<Services.SubscriptionService>();
            services.AddScoped<LemonSqueezyService>();
            services.AddScoped<GooglePlayService>();
            services.AddSingleton<ExchangeService>();
            services.AddScoped<IRuleEngine, RuleEngine>();
            services.AddScoped<Services.ProductService>();
            services.AddScoped<LicenseService>();
            services.AddScoped<GroupService>();

            if (string.IsNullOrEmpty(Configuration["KAFKA:BROKERS"]))
            {
                services.AddSingleton<ITransactionEventProducer, TransactionEventProducer>();
                services.AddSingleton<IPaymentEventProducer, TransactionEventProducer>();
            }
            else
            {
                services.AddSingleton<ITransactionEventProducer, KafkaTransactionEventProducer>();
                services.AddSingleton<IPaymentEventProducer, KafkaTransactionEventProducer>();
            }


            // Creating correct paypalEnvironment
            PayPalCheckoutSdk.Core.PayPalEnvironment environment;
            if (!string.IsNullOrEmpty(Configuration["PAYPAL:IS_SANDBOX"]) && bool.TryParse(Configuration["PAYPAL:IS_SANDBOX"], out bool isSandbox) && isSandbox)
            {
                Console.WriteLine("Starting paypal in SandboxMode");
                environment = new PayPalCheckoutSdk.Core.SandboxEnvironment(Configuration["PAYPAL:ID"], Configuration["PAYPAL:SECRET"]);
            }
            else
                environment = new PayPalCheckoutSdk.Core.LiveEnvironment(Configuration["PAYPAL:ID"], Configuration["PAYPAL:SECRET"]);
            services.AddSingleton(new PayPalCheckoutSdk.Core.PayPalHttpClient(environment));

            StripeConfiguration.ApiKey = Configuration["STRIPE:KEY"];
            services.AddSingleton<MigrationService>();
            services.AddHostedService(d=>d.GetRequiredService<MigrationService>());
            services.AddJaeger(0.1, 10);
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            app.UseDeveloperExceptionPage();
            app.UseSwagger();

            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "Payments v1");
                c.RoutePrefix = "api";
            });



            app.UseExceptionHandler(errorApp =>
            {
                errorApp.Run(async context =>
                {
                    context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    context.Response.ContentType = "text/json";

                    var exceptionHandlerPathFeature =
                        context.Features.Get<IExceptionHandlerPathFeature>();

                    if (exceptionHandlerPathFeature?.Error is ApiException ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await context.Response.WriteAsync(
                                        JsonConvert.SerializeObject(new { ex.Message }));
                        badRequestCount.Inc();
                    }
                    else
                    {
                        using var span = OpenTracing.Util.GlobalTracer.Instance.BuildSpan("error").WithTag("error", "true").StartActive();
                        span.Span.Log(exceptionHandlerPathFeature?.Error?.Message);
                        span.Span.Log(exceptionHandlerPathFeature?.Error?.StackTrace);
                        var traceId = Dns.GetHostName().Replace("payment", "").Trim('-') + "." + span.Span.Context.TraceId;
                        await context.Response.WriteAsync(
                            JsonConvert.SerializeObject(new
                            {
                                Slug = "internal_error",
                                Message = "An unexpected internal error occured. Please check that your request is valid. If it is please report he error and include the TraceId.",
                                TraceId = traceId
                            }));
                        errorCount.Inc();
                    }
                });
            });

            app.UseRouting();

            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapMetrics();
                endpoints.MapControllers();
            });
        }
    }
}
