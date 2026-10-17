using System.Security.Claims;
using System.Text.Json;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OficinaBilling.Api.Controllers;
using OficinaBilling.Api.Pagamentos;
using OficinaBilling.Api.Persistencia;
using OficinaBilling.Domain;
using OficinaMecanica.Contracts;

namespace OficinaBilling.Tests;

public class ControllerTests
{
    private readonly BillingDbContext _db = Apoio.NovoDb();
    private readonly Mock<IPublishEndpoint> _publish = new();
    private readonly Mock<IGatewayPagamento> _gateway = new();
    private readonly Guid _clienteId = Guid.NewGuid();

    private static T Com<T>(T controller, ClaimsPrincipal user) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }

    private PagamentosController Pagamentos(ClaimsPrincipal? user = null, IGatewayPagamento? gateway = null) =>
        Com(new PagamentosController(_db, _publish.Object, gateway ?? _gateway.Object, NullLogger<PagamentosController>.Instance),
            user ?? Apoio.Usuario("Administrador"));

    private async Task<(Orcamento, Pagamento)> SeedPagamento()
    {
        var orcamento = Apoio.NovoOrcamento(clienteId: _clienteId);
        orcamento.Aprovar();
        var pagamento = new Pagamento(orcamento.Id, orcamento.OrdemServicoId, orcamento.Valor);
        pagamento.DefinirCobranca("pref-1", "https://link");
        _db.AddRange(orcamento, pagamento);
        await _db.SaveChangesAsync();
        return (orcamento, pagamento);
    }

    // ---------- Orçamentos ----------

    [Fact]
    public async Task Orcamento_staff_ve_qualquer_um()
    {
        var (orcamento, _) = await SeedPagamento();
        var result = await Com(new OrcamentosController(_db), Apoio.Usuario("Mecanico")).Obter(orcamento.OrdemServicoId, default);

        Assert.Equal(orcamento.Id, Assert.IsType<Orcamento>(Assert.IsType<OkObjectResult>(result).Value).Id);
    }

    [Fact]
    public async Task Orcamento_cliente_so_ve_o_proprio()
    {
        var (orcamento, _) = await SeedPagamento();

        var dono = await Com(new OrcamentosController(_db), Apoio.Usuario("Cliente", _clienteId)).Obter(orcamento.OrdemServicoId, default);
        var outro = await Com(new OrcamentosController(_db), Apoio.Usuario("Cliente", Guid.NewGuid())).Obter(orcamento.OrdemServicoId, default);
        var semSub = await Com(new OrcamentosController(_db), Apoio.Usuario("Cliente")).Obter(orcamento.OrdemServicoId, default);

        Assert.IsType<OkObjectResult>(dono);
        Assert.IsType<ForbidResult>(outro);
        Assert.IsType<ForbidResult>(semSub);
    }

    [Fact]
    public async Task Orcamento_inexistente_404()
    {
        var result = await Com(new OrcamentosController(_db), Apoio.Usuario("Administrador")).Obter(Guid.NewGuid(), default);
        Assert.IsType<NotFoundResult>(result);
    }

    // ---------- Pagamentos: GET ----------

    [Fact]
    public async Task Pagamento_get_respeita_dono_e_404()
    {
        var (_, pagamento) = await SeedPagamento();

        Assert.IsType<OkObjectResult>(await Pagamentos(Apoio.Usuario("Cliente", _clienteId)).Obter(pagamento.OrdemServicoId, default));
        Assert.IsType<ForbidResult>(await Pagamentos(Apoio.Usuario("Cliente", Guid.NewGuid())).Obter(pagamento.OrdemServicoId, default));
        Assert.IsType<OkObjectResult>(await Pagamentos(Apoio.Usuario("Recepcionista")).Obter(pagamento.OrdemServicoId, default));
        Assert.IsType<NotFoundResult>(await Pagamentos().Obter(Guid.NewGuid(), default));
    }

    // ---------- Pagamentos: simular ----------

    [Fact]
    public async Task Simular_aprovado_publica_PagamentoAprovado()
    {
        var (_, pagamento) = await SeedPagamento();

        var result = await Pagamentos().Simular(pagamento.OrdemServicoId, new SimularPagamentoRequest(true, null), default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(StatusPagamento.Aprovado, pagamento.Status);
        _publish.Verify(p => p.Publish(new PagamentoAprovado(pagamento.OrdemServicoId, pagamento.Id, pagamento.Valor), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Simular_recusado_publica_PagamentoRecusado_com_motivo()
    {
        var (_, pagamento) = await SeedPagamento();

        await Pagamentos().Simular(pagamento.OrdemServicoId, new SimularPagamentoRequest(false, "sem saldo"), default);

        Assert.Equal(StatusPagamento.Recusado, pagamento.Status);
        _publish.Verify(p => p.Publish(new PagamentoRecusado(pagamento.OrdemServicoId, pagamento.Id, "sem saldo"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Simular_ja_resolvido_409_e_inexistente_404()
    {
        var (_, pagamento) = await SeedPagamento();
        await Pagamentos().Simular(pagamento.OrdemServicoId, new SimularPagamentoRequest(true, null), default);

        Assert.IsType<ConflictObjectResult>(await Pagamentos().Simular(pagamento.OrdemServicoId, new SimularPagamentoRequest(false, null), default));
        Assert.IsType<NotFoundResult>(await Pagamentos().Simular(Guid.NewGuid(), new SimularPagamentoRequest(true, null), default));
        _publish.Verify(p => p.Publish(It.IsAny<PagamentoRecusado>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------- Pagamentos: webhook ----------

    private static NotificacaoMercadoPago Notificacao(string id) =>
        new("payment", "payment.updated", new DadosNotificacao(JsonDocument.Parse($"\"{id}\"").RootElement));

    private void MpRetorna(string paymentId, string status, string? externalReference) =>
        _gateway.Setup(g => g.ConsultarPagamentoAsync(paymentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagamentoNoGateway(paymentId, status, "detalhe", externalReference));

    [Fact]
    public async Task Webhook_com_gateway_fake_ignora()
    {
        var (_, pagamento) = await SeedPagamento();

        var result = await Pagamentos(gateway: new GatewayPagamentoFake()).Webhook(Notificacao("1"), null, null, default);

        Assert.IsType<OkResult>(result);
        Assert.Equal(StatusPagamento.Pendente, pagamento.Status);
    }

    [Fact]
    public async Task Webhook_consulta_o_mp_e_aprova_pelo_external_reference()
    {
        var (_, pagamento) = await SeedPagamento();
        MpRetorna("555", "approved", pagamento.Id.ToString());

        Assert.IsType<OkResult>(await Pagamentos().Webhook(Notificacao("555"), null, null, default));
        Assert.IsType<OkResult>(await Pagamentos().Webhook(Notificacao("555"), null, null, default)); // reenvio do MP

        Assert.Equal(StatusPagamento.Aprovado, pagamento.Status);
        Assert.Equal("555", pagamento.GatewayId);
        _publish.Verify(p => p.Publish(It.IsAny<PagamentoAprovado>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Webhook_via_query_string_e_id_numerico()
    {
        var (_, pagamento) = await SeedPagamento();
        MpRetorna("777", "rejected", pagamento.Id.ToString());

        await Pagamentos().Webhook(null, "payment", "777", default);

        Assert.Equal(StatusPagamento.Recusado, pagamento.Status);
        Assert.Contains("rejected", pagamento.Motivo);
        _publish.Verify(p => p.Publish(It.IsAny<PagamentoRecusado>(), It.IsAny<CancellationToken>()), Times.Once);

        var numerico = new NotificacaoMercadoPago("payment", null, new DadosNotificacao(JsonDocument.Parse("777").RootElement));
        Assert.IsType<OkResult>(await Pagamentos().Webhook(numerico, null, null, default));
        _gateway.Verify(g => g.ConsultarPagamentoAsync("777", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Webhook_pagamento_pendente_no_mp_nao_resolve()
    {
        var (_, pagamento) = await SeedPagamento();
        MpRetorna("1", "in_process", pagamento.Id.ToString());

        await Pagamentos().Webhook(Notificacao("1"), null, null, default);

        Assert.Equal(StatusPagamento.Pendente, pagamento.Status);
        _publish.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Webhook_ignora_notificacoes_irrelevantes_ou_desconhecidas()
    {
        var (_, pagamento) = await SeedPagamento();
        MpRetorna("2", "approved", "nao-e-guid");
        MpRetorna("3", "approved", Guid.NewGuid().ToString());

        await Pagamentos().Webhook(new NotificacaoMercadoPago("merchant_order", null, null), null, null, default);
        await Pagamentos().Webhook(null, "payment", null, default);
        await Pagamentos().Webhook(Notificacao("inexistente"), null, null, default);
        await Pagamentos().Webhook(Notificacao("2"), null, null, default);
        await Pagamentos().Webhook(Notificacao("3"), null, null, default);

        Assert.Equal(StatusPagamento.Pendente, pagamento.Status);
        _publish.VerifyNoOtherCalls();
    }
}
