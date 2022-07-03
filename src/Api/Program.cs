using Api;
using Azure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.EventGrid.Models;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration.Extensions;
using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;

internal class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Configuration
            .AddAzureAppConfiguration(options =>
            {
                var connectionString = Environment.GetEnvironmentVariable("AppConfiguration__ConnectionString");

                options
                    .Connect(connectionString)
                    .Select("Appc:*", "configs")
                    .ConfigureKeyVault(kv =>
                    {
                        var credentials = new DefaultAzureCredential();
#if DEBUG
                        var options = new DefaultAzureCredentialOptions { VisualStudioTenantId = Environment.GetEnvironmentVariable("VS_TENANT_ID") };
                        credentials = new DefaultAzureCredential(options);
#endif

                        kv.SetCredential(credentials);
                        kv.SetSecretRefreshInterval(TimeSpan.FromSeconds(10));
                    })

                    .ConfigureRefresh(x =>
                    {
                        x.Register("Appc:Sentinel", "configs", true);
                        x.SetCacheExpiration(TimeSpan.FromDays(1));
                    });
            });

        builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Trace);

        builder.Services.Configure<AppcOptions>(builder.Configuration.GetSection("Appc"));

        builder.Services.AddAzureAppConfiguration();

        var app = builder.Build();

        app.UseAzureAppConfiguration();

        app.MapGet("/api", async (
            IOptionsSnapshot<AppcOptions> options,
            IConfiguration configuration) =>
        {
            var temp = configuration["Appc:Foo"];

            var foo = options.Value.Foo;
            var secret = options.Value.FooSecret;

            return Results.Ok(new { foo = foo, foo_secret = secret });
        });

        app.MapPost(
            "/refresh",
            async (
                [FromBody] Azure.Messaging.EventGrid.EventGridEvent[] request,
                [FromServices] IConfigurationRefresherProvider refreshProvider) =>
            {
                var eg = request.First();

                if (eg.EventType == "Microsoft.EventGrid.SubscriptionValidationEvent")
                {
                    var data = eg.Data.ToObjectFromJson<JsonObject>();
                    var responseData = new SubscriptionValidationResponse()
                    {
                        ValidationResponse = data["validationCode"].ToString()
                    };

                    return Results.Ok(responseData);
                }

                eg.TryCreatePushNotification(out var notification);

                if (notification == null)
                    return Results.Ok();

                foreach (var refresher in refreshProvider.Refreshers)
                {
                    refresher.ProcessPushNotification(notification, TimeSpan.FromSeconds(1));
                }

                return Results.Ok();
            });

        app.Run();
    }
}