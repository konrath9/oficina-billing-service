using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OficinaBilling.Domain;

namespace OficinaBilling.Api.Pagamentos;

/// <summary>
/// Integração com o Mercado Pago (Checkout Pro) via REST, sem SDK.
/// Docs: https://www.mercadopago.com.br/developers/pt/reference
/// </summary>
public class MercadoPagoGateway(HttpClient http, IConfiguration configuration) : IGatewayPagamento
{
    private static readonly JsonSerializerOptions SemNulos = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public async Task<CobrancaCriada> CriarCobrancaAsync(Pagamento pagamento, Orcamento orcamento, CancellationToken ct)
    {
        // Itens do orçamento quando somam o valor cobrado; senão uma linha com o total
        // (o MP cobra a soma dos itens, então ela precisa bater com o pagamento).
        var itens = orcamento.Itens.Sum(i => i.Quantidade * i.PrecoUnitario) == pagamento.Valor
            ? orcamento.Itens.Select(i => new ItemPreferencia(i.Descricao, i.Quantidade, i.PrecoUnitario)).ToArray()
            : [new ItemPreferencia($"Ordem de serviço {orcamento.NumeroOs}", 1, pagamento.Valor)];

        var preferencia = new
        {
            items = itens,
            payer = orcamento.EmailCliente is null ? null : new { email = orcamento.EmailCliente },
            external_reference = pagamento.Id.ToString(),
            notification_url = NullSeVazio(configuration["MercadoPago:NotificationUrl"])
        };

        using var response = await http.PostAsJsonAsync("checkout/preferences", preferencia, SemNulos, ct);
        await GarantirSucesso(response, "criar preferência", ct);

        var criada = await response.Content.ReadFromJsonAsync<PreferenciaResponse>(ct)
            ?? throw new InvalidOperationException("Resposta vazia do Mercado Pago ao criar preferência.");

        // Credenciais de teste (TEST-...) usam o checkout sandbox.
        var token = configuration["MercadoPago:AccessToken"] ?? string.Empty;
        var link = token.StartsWith("TEST-", StringComparison.Ordinal)
            ? criada.SandboxInitPoint ?? criada.InitPoint
            : criada.InitPoint;
        return new CobrancaCriada(criada.Id, link);
    }

    public async Task<PagamentoNoGateway?> ConsultarPagamentoAsync(string paymentId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"v1/payments/{Uri.EscapeDataString(paymentId)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await GarantirSucesso(response, "consultar pagamento", ct);

        var pagamento = await response.Content.ReadFromJsonAsync<PaymentResponse>(ct);
        return pagamento is null
            ? null
            : new PagamentoNoGateway(pagamento.Id.ToString(), pagamento.Status ?? string.Empty, pagamento.StatusDetail, pagamento.ExternalReference);
    }

    public async Task EstornarAsync(string gatewayId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"v1/payments/{Uri.EscapeDataString(gatewayId)}/refunds")
        {
            Content = JsonContent.Create(new { })
        };
        // Mesma chave em retentativas: o MP não estorna duas vezes.
        request.Headers.Add("X-Idempotency-Key", $"refund-{gatewayId}");

        using var response = await http.SendAsync(request, ct);
        await GarantirSucesso(response, "estornar pagamento", ct);
    }

    private static string? NullSeVazio(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor;

    private static async Task GarantirSucesso(HttpResponseMessage response, string operacao, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var corpo = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"Mercado Pago falhou ao {operacao}: {(int)response.StatusCode} {corpo}", null, response.StatusCode);
    }

    private sealed record ItemPreferencia(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("quantity")] int Quantity,
        [property: JsonPropertyName("unit_price")] decimal UnitPrice)
    {
        [JsonPropertyName("currency_id")] public string CurrencyId => "BRL";
    }

    private sealed record PreferenciaResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("init_point")] string? InitPoint,
        [property: JsonPropertyName("sandbox_init_point")] string? SandboxInitPoint);

    private sealed record PaymentResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("status_detail")] string? StatusDetail,
        [property: JsonPropertyName("external_reference")] string? ExternalReference);
}
