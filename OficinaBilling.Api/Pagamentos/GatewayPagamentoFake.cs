using OficinaBilling.Domain;

namespace OficinaBilling.Api.Pagamentos;

/// <summary>
/// Gateway usado quando não há credenciais do Mercado Pago. A confirmação do pagamento vem do
/// endpoint POST /api/pagamentos/{ordemServicoId}/simular; o webhook é ignorado.
/// </summary>
public class GatewayPagamentoFake : IGatewayPagamento
{
    public Task<CobrancaCriada> CriarCobrancaAsync(Pagamento pagamento, Orcamento orcamento, CancellationToken ct)
    {
        var id = $"fake-{pagamento.Id:N}";
        return Task.FromResult(new CobrancaCriada(id, $"https://pagamento-fake.local/checkout/{id}"));
    }

    public Task<PagamentoNoGateway?> ConsultarPagamentoAsync(string paymentId, CancellationToken ct) =>
        Task.FromResult<PagamentoNoGateway?>(null);

    public Task EstornarAsync(string gatewayId, CancellationToken ct) => Task.CompletedTask;
}
