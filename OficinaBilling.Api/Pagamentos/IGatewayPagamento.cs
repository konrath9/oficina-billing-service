using OficinaBilling.Domain;

namespace OficinaBilling.Api.Pagamentos;

public record CobrancaCriada(string GatewayId, string? LinkPagamento);

/// <summary>Pagamento como o gateway o vê (status no vocabulário do Mercado Pago: approved, rejected, cancelled, pending...).</summary>
public record PagamentoNoGateway(string Id, string Status, string? StatusDetalhe, string? ExternalReference);

public interface IGatewayPagamento
{
    Task<CobrancaCriada> CriarCobrancaAsync(Pagamento pagamento, Orcamento orcamento, CancellationToken ct);
    Task<PagamentoNoGateway?> ConsultarPagamentoAsync(string paymentId, CancellationToken ct);
    Task EstornarAsync(string gatewayId, CancellationToken ct);
}

public static class GatewayPagamentoExtensions
{
    public const string MercadoPagoBaseUrl = "https://api.mercadopago.com/";

    /// <summary>Mercado Pago quando há access token configurado; senão o gateway fake (dev, CI e demo).</summary>
    public static IServiceCollection AddGatewayPagamento(this IServiceCollection services, IConfiguration configuration)
    {
        var accessToken = configuration["MercadoPago:AccessToken"];
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            services.AddSingleton<IGatewayPagamento, GatewayPagamentoFake>();
            return services;
        }

        services.AddHttpClient<IGatewayPagamento, MercadoPagoGateway>(http =>
        {
            http.BaseAddress = new Uri(MercadoPagoBaseUrl);
            http.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);
        });
        return services;
    }
}
