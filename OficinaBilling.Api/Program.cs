using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using OficinaBilling.Api;
using OficinaBilling.Api.Pagamentos;
using OficinaBilling.Api.Persistencia;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

const string ServiceName = "oficina-billing-service";

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ── Logs estruturados (JSON) ─────────────────────────────────────
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()),
    writeToProviders: true);

// ── Banco de dados ───────────────────────────────────────────────
builder.Services.AddDbContext<BillingDbContext>(options =>
    options.UseNpgsql(config.GetConnectionString("DefaultConnection"),
        npgsql => npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null)));

builder.Services.AddGatewayPagamento(config);

// ── Mensageria (MassTransit + RabbitMQ, outbox EF) ───────────────
builder.Services.AddMassTransit(x =>
{
    x.SetKebabCaseEndpointNameFormatter();
    x.AddConsumers(typeof(Program).Assembly);

    // Bus outbox: o Publish feito nos controllers só sai quando o SaveChanges commita.
    x.AddEntityFrameworkOutbox<BillingDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });
    // Inbox/outbox nos consumers: deduplica por MessageId e publica junto com o commit.
    x.AddConfigureEndpointsCallback((context, _, endpoint) =>
        endpoint.UseEntityFrameworkOutbox<BillingDbContext>(context));

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(config["RabbitMq:Host"] ?? "localhost", "/", h =>
        {
            h.Username(config["RabbitMq:Username"] ?? "oficina");
            h.Password(config["RabbitMq:Password"] ?? "oficina123");
        });
        cfg.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(2)));
        cfg.ConfigureEndpoints(context);
    });
});

// ── JWT (mesmos parâmetros do OS Service) ────────────────────────
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = config["Jwt:Issuer"],
            ValidAudience = config["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
                config["Jwt:SecretKey"] ?? throw new InvalidOperationException("Jwt:SecretKey não configurado."))),
            ClockSkew = TimeSpan.Zero
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddHealthChecks().AddDbContextCheck<BillingDbContext>();

// ── Observabilidade (New Relic via OpenTelemetry/OTLP) ───────────
// Só habilita com license key: local/CI rodam sem exportar nada.
var newRelicLicenseKey = config["NewRelic:LicenseKey"];
if (!string.IsNullOrWhiteSpace(newRelicLicenseKey))
{
    var otlpEndpoint = config["NewRelic:OtlpEndpoint"] ?? "https://otlp.nr-data.net:4317";

    void ConfigurarOtlp(OpenTelemetry.Exporter.OtlpExporterOptions otlp)
    {
        otlp.Endpoint = new Uri(otlpEndpoint);
        otlp.Headers = $"api-key={newRelicLicenseKey}";
    }

    builder.Logging.AddOpenTelemetry(logging =>
    {
        logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(ServiceName));
        logging.IncludeScopes = true;
        logging.IncludeFormattedMessage = true;
        logging.AddOtlpExporter(ConfigurarOtlp);
    });

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(ServiceName))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSource("MassTransit")
            .AddOtlpExporter(ConfigurarOtlp))
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter("MassTransit")
            .AddMeter(BillingMetrics.MeterName)
            .AddOtlpExporter(ConfigurarOtlp));
}

builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Oficina Mecânica - Billing Service",
        Version = "v1",
        Description = "Orçamentos e pagamentos (Mercado Pago). Participante da Saga orquestrada pelo OS Service."
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Informe o token JWT emitido pelo OS Service"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        }
    });

    options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml"));
});

var app = builder.Build();

// Aplica migrations ao iniciar (ignorado nos testes com InMemory)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
    if (db.Database.IsRelational())
        db.Database.Migrate();
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();

// Swagger sob /billing para não colidir com o OS Service no Ingress
app.UseSwagger(c => c.RouteTemplate = "billing/swagger/{documentName}/swagger.json");
app.UseSwaggerUI(c =>
{
    c.RoutePrefix = "billing/swagger";
    c.SwaggerEndpoint("v1/swagger.json", "Billing Service v1");
});

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

// Necessário para WebApplicationFactory nos testes
public partial class Program { }
