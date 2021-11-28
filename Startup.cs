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
using System.Linq;

namespace Coflnet.Payments
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {

            services.AddControllers().AddNewtonsoftJson();
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "Payments", Version = "0.0.1", License = new OpenApiLicense { Name = "MIT" } });
                // Set the comments path for the Swagger JSON and UI.
                var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
                var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
                c.IncludeXmlComments(xmlPath);
            });

            var serverVersion = new MariaDbServerVersion(new Version(Configuration["MARIADB_VERSION"]));
            services.AddDbContext<PaymentContext>(
                dbContextOptions => dbContextOptions
                    .UseMySql(Configuration["DB_CONNECTION"], serverVersion)
                    .EnableSensitiveDataLogging() // <-- These two calls are optional but help
                    .EnableDetailedErrors()       // <-- with debugging (remove for production).
            );
            services.AddScoped<TransactionService>();
            services.AddScoped<UserService>();
            services.AddSingleton<ExchangeService>();
            services.AddScoped<Services.ProductService>();

            if (string.IsNullOrEmpty(Configuration["KAFKA_HOST"]))
                services.AddSingleton<ITransactionEventProducer, TransactionEventProducer>();
            else
                services.AddSingleton<ITransactionEventProducer, KafkaTransactionEventProducer>();


            // Creating correct paypalEnvironment
            PayPalCheckoutSdk.Core.PayPalEnvironment environment;
            if (!string.IsNullOrEmpty(Configuration["PAYPAL:IS_SANDBOX"]) && bool.TryParse(Configuration["PAYPAL:IS_SANDBOX"], out bool isSandbox) && isSandbox)
                environment = new PayPalCheckoutSdk.Core.SandboxEnvironment(Configuration["PAYPAL:ID"], Configuration["PAYPAL:SECRET"]);
            else
                environment = new PayPalCheckoutSdk.Core.LiveEnvironment(Configuration["PAYPAL:ID"], Configuration["PAYPAL:SECRET"]);
            services.AddSingleton<PayPalCheckoutSdk.Core.PayPalHttpClient>(new PayPalCheckoutSdk.Core.PayPalHttpClient(environment));

            StripeConfiguration.ApiKey = Configuration["STRIPE:SIGNING_SECRET"];
            services.AddHostedService<MigrationService>();
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



            //app.UseHttpsRedirection();

            app.UseRouting();

            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });
        }
    }
}
