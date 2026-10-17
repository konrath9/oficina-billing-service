using System.Diagnostics.Metrics;

namespace OficinaBilling.Api;

/// <summary>Métricas de negócio exportadas via OpenTelemetry (New Relic).</summary>
public static class BillingMetrics
{
    public const string MeterName = "OficinaBilling";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> PagamentosAprovados = Meter.CreateCounter<long>("pagamentos.aprovados");
    public static readonly Counter<long> PagamentosRecusados = Meter.CreateCounter<long>("pagamentos.recusados");
    public static readonly Counter<long> PagamentosEstornados = Meter.CreateCounter<long>("pagamentos.estornados");
}
