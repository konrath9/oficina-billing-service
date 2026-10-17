using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OficinaBilling.Api.Pagamentos;
using OficinaBilling.Domain;

namespace OficinaBilling.Tests;

public class GatewaySelecaoTests
{
    private static IGatewayPagamento Resolver(string? accessToken)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MercadoPago:AccessToken"] = accessToken })
            .Build();
        return new ServiceCollection()
            .AddSingleton<IConfiguration>(config)
            .AddGatewayPagamento(config)
            .BuildServiceProvider()
            .GetRequiredService<IGatewayPagamento>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sem_access_token_usa_gateway_fake(string? token) =>
        Assert.IsType<GatewayPagamentoFake>(Resolver(token));

    [Fact]
    public void Com_access_token_usa_mercado_pago() =>
        Assert.IsType<MercadoPagoGateway>(Resolver("TEST-123"));

    [Fact]
    public async Task Gateway_fake_gera_link_e_estorna_sempre()
    {
        var fake = new GatewayPagamentoFake();
        var pagamento = new Pagamento(Guid.NewGuid(), Guid.NewGuid(), 10m);

        var cobranca = await fake.CriarCobrancaAsync(pagamento, Apoio.NovoOrcamento(), default);

        Assert.StartsWith("fake-", cobranca.GatewayId);
        Assert.Contains(cobranca.GatewayId, cobranca.LinkPagamento);
        Assert.Null(await fake.ConsultarPagamentoAsync("1", default));
        await fake.EstornarAsync("fake-1", default);
    }
}

public class MercadoPagoGatewayTests
{
    private sealed class HandlerFake(HttpStatusCode status, string corpo) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Chamadas { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Chamadas.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(status) { Content = new StringContent(corpo, Encoding.UTF8, "application/json") };
        }
    }

    private static (MercadoPagoGateway Gateway, HandlerFake Handler) Criar(HttpStatusCode status, string corpo,
        string token = "TEST-abc", string? notificationUrl = "https://billing/api/pagamentos/webhook")
    {
        var handler = new HandlerFake(status, corpo);
        var http = new HttpClient(handler) { BaseAddress = new Uri(GatewayPagamentoExtensions.MercadoPagoBaseUrl) };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MercadoPago:AccessToken"] = token,
            ["MercadoPago:NotificationUrl"] = notificationUrl
        }).Build();
        return (new MercadoPagoGateway(http, config), handler);
    }

    private const string PreferenciaJson = """{"id":"pref-1","init_point":"https://mp/prod","sandbox_init_point":"https://mp/sandbox"}""";

    [Fact]
    public async Task CriarCobranca_envia_itens_external_reference_e_notification_url()
    {
        var (gateway, handler) = Criar(HttpStatusCode.Created, PreferenciaJson);
        var orcamento = Apoio.NovoOrcamento(valor: 150m); // itens somam 150
        var pagamento = new Pagamento(orcamento.Id, orcamento.OrdemServicoId, 150m);

        var cobranca = await gateway.CriarCobrancaAsync(pagamento, orcamento, default);

        Assert.Equal("pref-1", cobranca.GatewayId);
        Assert.Equal("https://mp/sandbox", cobranca.LinkPagamento); // token TEST- => sandbox

        var (request, body) = Assert.Single(handler.Chamadas);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.mercadopago.com/checkout/preferences", request.RequestUri!.ToString());
        using var json = JsonDocument.Parse(body!);
        Assert.Equal(pagamento.Id.ToString(), json.RootElement.GetProperty("external_reference").GetString());
        Assert.Equal("https://billing/api/pagamentos/webhook", json.RootElement.GetProperty("notification_url").GetString());
        Assert.Equal("cliente@teste.com", json.RootElement.GetProperty("payer").GetProperty("email").GetString());
        var itens = json.RootElement.GetProperty("items");
        Assert.Equal(2, itens.GetArrayLength());
        Assert.Equal("BRL", itens[0].GetProperty("currency_id").GetString());
        Assert.Equal(25m, itens[1].GetProperty("unit_price").GetDecimal());
    }

    [Fact]
    public async Task CriarCobranca_usa_linha_unica_quando_itens_nao_somam_o_valor_e_init_point_em_producao()
    {
        var (gateway, handler) = Criar(HttpStatusCode.Created, PreferenciaJson, token: "APP_USR-xyz", notificationUrl: "");
        var orcamento = Apoio.NovoOrcamento(valor: 140m); // itens somam 150, com desconto
        var pagamento = new Pagamento(orcamento.Id, orcamento.OrdemServicoId, 140m);

        var cobranca = await gateway.CriarCobrancaAsync(pagamento, orcamento, default);

        Assert.Equal("https://mp/prod", cobranca.LinkPagamento);
        using var json = JsonDocument.Parse(handler.Chamadas[0].Body!);
        var itens = json.RootElement.GetProperty("items");
        Assert.Equal(1, itens.GetArrayLength());
        Assert.Equal(140m, itens[0].GetProperty("unit_price").GetDecimal());
        Assert.False(json.RootElement.TryGetProperty("notification_url", out _));
    }

    [Fact]
    public async Task CriarCobranca_com_erro_lanca_excecao()
    {
        var (gateway, _) = Criar(HttpStatusCode.Unauthorized, """{"message":"invalid token"}""");
        var orcamento = Apoio.NovoOrcamento();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            gateway.CriarCobrancaAsync(new Pagamento(orcamento.Id, orcamento.OrdemServicoId, 150m), orcamento, default));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("invalid token", ex.Message);
    }

    [Fact]
    public async Task ConsultarPagamento_mapeia_resposta()
    {
        var (gateway, handler) = Criar(HttpStatusCode.OK,
            """{"id":123456,"status":"approved","status_detail":"accredited","external_reference":"abc"}""");

        var pagamento = await gateway.ConsultarPagamentoAsync("123456", default);

        Assert.Equal(new PagamentoNoGateway("123456", "approved", "accredited", "abc"), pagamento);
        Assert.Equal("https://api.mercadopago.com/v1/payments/123456", handler.Chamadas[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task ConsultarPagamento_inexistente_retorna_null()
    {
        var (gateway, _) = Criar(HttpStatusCode.NotFound, "{}");
        Assert.Null(await gateway.ConsultarPagamentoAsync("1", default));
    }

    [Fact]
    public async Task Estornar_chama_refunds_com_chave_de_idempotencia()
    {
        var (gateway, handler) = Criar(HttpStatusCode.Created, """{"id":1}""");

        await gateway.EstornarAsync("123456", default);

        var request = handler.Chamadas[0].Request;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.mercadopago.com/v1/payments/123456/refunds", request.RequestUri!.ToString());
        Assert.Equal("refund-123456", request.Headers.GetValues("X-Idempotency-Key").Single());
    }

    [Fact]
    public async Task Estornar_com_erro_lanca_excecao()
    {
        var (gateway, _) = Criar(HttpStatusCode.BadRequest, "{}");
        await Assert.ThrowsAsync<HttpRequestException>(() => gateway.EstornarAsync("1", default));
    }
}
