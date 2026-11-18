using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Npgsql;
using Pulsar.Client.Api;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Rockets.Api.Configuration;
using Rockets.Api.Messaging;
using Rockets.Api.Repositories;
using Rockets.Api.Requests;
using Rockets.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<RocketOptions>()
    .Bind(builder.Configuration.GetSection(RocketOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IUnprocessableMessageStore, UnprocessableMessageStore>();
builder.Services.AddSingleton<RocketService>();

// Postgres: event store + current state of each rocket.
builder.Services.AddSingleton(sp =>
    NpgsqlDataSource.Create(sp.GetRequiredService<IOptions<RocketOptions>>().Value.PostgresConnectionString));
builder.Services.AddSingleton<IRocketRepository, RocketRepository>();
builder.Services.AddSingleton<IOutboxRepository, OutboxRepository>();

// Pulsar: the ingest API publishes, the background worker consumes and applies.
builder.Services.AddSingleton(sp => new PulsarClientBuilder()
    .ServiceUrl(sp.GetRequiredService<IOptions<RocketOptions>>().Value.PulsarServiceUrl)
    .BuildAsync().GetAwaiter().GetResult());
builder.Services.AddSingleton<IRocketEventPublisher, PulsarRocketEventPublisher>();
builder.Services.AddSingleton<RocketEventProcessor>();
builder.Services.AddHostedService<RocketEventConsumer>();
builder.Services.AddHostedService<OutboxRelay>();   // publishes what was stored in the outbox while Pulsar was down
builder.Services.AddSingleton<RocketMetrics>();

// OpenTelemetry (metrics + logs) only when an OTLP endpoint is configured: compose.yaml sets it to the Aspire dashboard,
// and so does the "dashboard" launch profile. Without it (plain `dotnet run`) nothing is exported.
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("rockets-api"))
        .WithMetrics(metrics => metrics
            .AddMeter(RocketMetrics.MeterName)                       // rockets.messages, by result
            .AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "System.Runtime"))
        .WithLogging(_ => { }, options =>
        {
            options.IncludeFormattedMessage = true;   // readable text, not just the template
            options.IncludeScopes = true;             // e.g. the raw "Body" of an unprocessable message (see RocketService.Receive)
        })
        .UseOtlpExporter();

    // The dashboard also gets the per-message Debug lines ("Message X#5 (RocketSpeedIncreased): Applied"); the console keeps
    // Information. Under full load the exporter drops log records rather than slowing ingestion down.
    builder.Logging.AddFilter<OpenTelemetryLoggerProvider>("Rockets.Api", LogLevel.Debug);
}

builder.Services.AddControllers().AddJsonOptions(options => ConfigureJson(options.JsonSerializerOptions));
// OpenAPI schema generation reads the minimal-API JSON options, so apply the same settings there too.
builder.Services.ConfigureHttpJsonOptions(options => ConfigureJson(options.SerializerOptions));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Rockets API";
        document.Info.Description =
            "Receives rocket messages.";
        return Task.CompletedTask;
    });

    // MessagesController reads the raw body (to keep invalid messages verbatim), so ASP.NET can't infer
    // the request type. Describe it explicitly so Swagger UI shows the message format.
    options.AddOperationTransformer(async (operation, context, cancellationToken) =>
    {
        if (context.Description is { HttpMethod: "POST", RelativePath: "messages" })
        {
            var schema = await context.GetOrCreateSchemaAsync(typeof(RocketMessageRequest), null, cancellationToken);
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = schema } },
            };
        }
    });
});
builder.Services.AddHealthChecks();

var app = builder.Build();

// Create the tables if needed. Nothing to restore: the state lives in Postgres, and unacknowledged events in Pulsar.
await app.Services.GetRequiredService<IRocketRepository>().InitializeAsync();
await app.Services.GetRequiredService<IOutboxRepository>().InitializeAsync();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "Rockets API");
    options.DocumentTitle = "Rockets API";
});
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
app.MapHealthChecks("/health");
app.MapControllers();

app.Run();

static void ConfigureJson(JsonSerializerOptions options)
{
    // Enums as names ("Launched"), not numbers.
    options.Converters.Add(new JsonStringEnumConverter());
    // Web defaults accept numbers sent as strings, which also makes OpenAPI document them as "integer | string".
    options.NumberHandling = JsonNumberHandling.Strict;
}
