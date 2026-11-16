using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Moq;
using OficinaBilling.Api.Pagamentos;
using OficinaBilling.Domain;
using OficinaMecanica.Contracts;

namespace OficinaBilling.Tests;

public class ConsumerTests : IAsyncLifetime
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly Mock<IGatewayPagamento> _gateway = new();
    private Microsoft.Extensions.DependencyInjection.ServiceProvider _provider = null!;
    private ITestHarness _harness = null!;

    public async Task InitializeAsync()
    {
        _gateway.Setup(g => g.CriarCobrancaAsync(It.IsAny<Pagamento>(), It.IsAny<Orcamento>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CobrancaCriada("pref-123", "https://checkout/pref-123"));
        (_provider, _harness) = await Apoio.NovoHarness(_gateway.Object, _dbName);
    }

    public async Task DisposeAsync()
    {
        await _harness.Stop();
        await _provider.DisposeAsync();
    }

    private static GerarOrcamento Comando(Guid osId, decimal valor = 150m, IReadOnlyList<ItemOrcamento>? itens = null) =>
        new(osId, "OS-0001", Guid.NewGuid(), "c@t.com",
            itens ?? [new ItemOrcamento("Troca de óleo", "Servico", 1, 100m), new ItemOrcamento("Filtro", "Peca", 2, 25m)], valor);

    private async Task Seed(params object[] entidades)
    {
        await using var db = Apoio.NovoDb(_dbName);
        db.AddRange(entidades);
        await db.SaveChangesAsync();
    }

    private async Task<T> Consumir<T>(T mensagem) where T : class
    {
        var messageId = NewId.NextGuid();
        await _harness.Bus.Publish(mensagem, ctx => ctx.MessageId = messageId);
        Assert.True(await _harness.Consumed.Any<T>(x => x.Context.MessageId == messageId));
        return mensagem;
    }

    // ---------- GerarOrcamento ----------

    [Fact]
    public async Task GerarOrcamento_cria_orcamento_e_publica_OrcamentoGerado()
    {
        var osId = Guid.NewGuid();
        await Consumir(Comando(osId));

        Assert.True(await _harness.Published.Any<OrcamentoGerado>(x => x.Context.Message.OrdemServicoId == osId && x.Context.Message.Valor == 150m));
        await using var db = Apoio.NovoDb(_dbName);
        var orcamento = await db.Orcamentos.SingleAsync(o => o.OrdemServicoId == osId);
        Assert.Equal(StatusOrcamento.Pendente, orcamento.Status);
        Assert.Equal(2, orcamento.Itens.Count);
    }

    [Fact]
    public async Task GerarOrcamento_repetido_e_idempotente()
    {
        var osId = Guid.NewGuid();
        await Consumir(Comando(osId));
        await Consumir(Comando(osId));

        Assert.Equal(2, _harness.Published.Select<OrcamentoGerado>(x => x.Context.Message.OrdemServicoId == osId).Count());
        var ids = _harness.Published.Select<OrcamentoGerado>().Select(x => x.Context.Message.OrcamentoId).Distinct();
        Assert.Single(ids);
        await using var db = Apoio.NovoDb(_dbName);
        Assert.Equal(1, await db.Orcamentos.CountAsync(o => o.OrdemServicoId == osId));
    }

    [Fact]
    public async Task GerarOrcamento_invalido_publica_OrcamentoFalhou()
    {
        var semValor = Guid.NewGuid();
        var semItens = Guid.NewGuid();
        await Consumir(Comando(semValor, valor: 0));
        await Consumir(Comando(semItens, itens: []));

        Assert.True(await _harness.Published.Any<OrcamentoFalhou>(x => x.Context.Message.OrdemServicoId == semValor));
        Assert.True(await _harness.Published.Any<OrcamentoFalhou>(x => x.Context.Message.OrdemServicoId == semItens));
        await using var db = Apoio.NovoDb(_dbName);
        Assert.False(await db.Orcamentos.AnyAsync());
    }

    // ---------- AprovarOrcamento ----------

    [Fact]
    public async Task AprovarOrcamento_aprova_cria_cobranca_e_publica_PagamentoCriado()
    {
        var orcamento = Apoio.NovoOrcamento();
        await Seed(orcamento);

        await Consumir(new AprovarOrcamento(orcamento.OrdemServicoId));

        Assert.True(await _harness.Published.Any<PagamentoCriado>(x =>
            x.Context.Message.OrdemServicoId == orcamento.OrdemServicoId && x.Context.Message.LinkPagamento == "https://checkout/pref-123"));
        await using var db = Apoio.NovoDb(_dbName);
        Assert.Equal(StatusOrcamento.Aprovado, (await db.Orcamentos.SingleAsync()).Status);
        var pagamento = await db.Pagamentos.SingleAsync();
        Assert.Equal(StatusPagamento.Pendente, pagamento.Status);
        Assert.Equal("pref-123", pagamento.GatewayId);
        Assert.Equal(orcamento.Valor, pagamento.Valor);
    }

    [Fact]
    public async Task AprovarOrcamento_repetido_nao_cria_segunda_cobranca()
    {
        var orcamento = Apoio.NovoOrcamento();
        await Seed(orcamento);

        await Consumir(new AprovarOrcamento(orcamento.OrdemServicoId));
        await Consumir(new AprovarOrcamento(orcamento.OrdemServicoId));

        Assert.Equal(2, _harness.Published.Select<PagamentoCriado>().Count());
        _gateway.Verify(g => g.CriarCobrancaAsync(It.IsAny<Pagamento>(), It.IsAny<Orcamento>(), It.IsAny<CancellationToken>()), Times.Once);
        await using var db = Apoio.NovoDb(_dbName);
        Assert.Equal(1, await db.Pagamentos.CountAsync());
    }

    [Fact]
    public async Task AprovarOrcamento_cancelado_e_ignorado()
    {
        var orcamento = Apoio.NovoOrcamento();
        orcamento.Cancelar("cliente reprovou");
        await Seed(orcamento);

        await Consumir(new AprovarOrcamento(orcamento.OrdemServicoId));

        Assert.False(await _harness.Published.Any<PagamentoCriado>());
        await using var db = Apoio.NovoDb(_dbName);
        Assert.False(await db.Pagamentos.AnyAsync());
    }

    [Fact]
    public async Task AprovarOrcamento_inexistente_falha()
    {
        await Consumir(new AprovarOrcamento(Guid.NewGuid()));

        var consumido = _harness.Consumed.Select<AprovarOrcamento>().Single();
        Assert.IsType<InvalidOperationException>(consumido.Exception);
    }

    // ---------- CancelarOrcamento (compensação) ----------

    [Fact]
    public async Task CancelarOrcamento_cancela_orcamento_e_pagamento_pendente()
    {
        var orcamento = Apoio.NovoOrcamento();
        orcamento.Aprovar();
        var pagamento = new Pagamento(orcamento.Id, orcamento.OrdemServicoId, orcamento.Valor);
        await Seed(orcamento, pagamento);

        await Consumir(new CancelarOrcamento(orcamento.OrdemServicoId, "pagamento recusado"));
        await Consumir(new CancelarOrcamento(orcamento.OrdemServicoId, "repetido")); // idempotente

        await using var db = Apoio.NovoDb(_dbName);
        var o = await db.Orcamentos.SingleAsync();
        Assert.Equal(StatusOrcamento.Cancelado, o.Status);
        Assert.Equal("pagamento recusado", o.MotivoCancelamento);
        Assert.Equal(StatusPagamento.Cancelado, (await db.Pagamentos.SingleAsync()).Status);
        Assert.All(_harness.Consumed.Select<CancelarOrcamento>(), c => Assert.Null(c.Exception));
    }

    [Fact]
    public async Task CancelarOrcamento_sem_orcamento_nao_falha_e_mantem_pagamento_recusado()
    {
        var osId = Guid.NewGuid();
        var pagamento = new Pagamento(Guid.NewGuid(), osId, 10m);
        pagamento.Recusar("cartão recusado");
        await Seed(pagamento);

        await Consumir(new CancelarOrcamento(osId, "pagamento recusado"));

        Assert.Null(_harness.Consumed.Select<CancelarOrcamento>().Single().Exception);
        await using var db = Apoio.NovoDb(_dbName);
        Assert.Equal(StatusPagamento.Recusado, (await db.Pagamentos.SingleAsync()).Status);
    }

    // ---------- EstornarPagamento (compensação) ----------

    [Fact]
    public async Task EstornarPagamento_estorna_no_gateway_e_publica_PagamentoEstornado()
    {
        var pagamento = new Pagamento(Guid.NewGuid(), Guid.NewGuid(), 150m);
        pagamento.DefinirCobranca("pref-1", null);
        pagamento.Aprovar("999");
        await Seed(pagamento);

        await Consumir(new EstornarPagamento(pagamento.OrdemServicoId, "execução falhou"));
        await Consumir(new EstornarPagamento(pagamento.OrdemServicoId, "repetido"));

        Assert.Equal(2, _harness.Published.Select<PagamentoEstornado>(x => x.Context.Message.PagamentoId == pagamento.Id).Count());
        _gateway.Verify(g => g.EstornarAsync("999", It.IsAny<CancellationToken>()), Times.Once);
        await using var db = Apoio.NovoDb(_dbName);
        var p = await db.Pagamentos.SingleAsync();
        Assert.Equal(StatusPagamento.Estornado, p.Status);
        Assert.Equal("execução falhou", p.Motivo);
    }

    [Fact]
    public async Task EstornarPagamento_aprovado_por_simulacao_estorna_sem_chamar_gateway()
    {
        // Gateway real configurado, mas o pagamento foi aprovado via /simular: só existe o preference id,
        // que o refund do Mercado Pago não aceita. A Saga não pode ficar presa em Estornando.
        var pagamento = new Pagamento(Guid.NewGuid(), Guid.NewGuid(), 150m);
        pagamento.DefinirCobranca("pref-1", null);
        pagamento.Aprovar();
        await Seed(pagamento);

        await Consumir(new EstornarPagamento(pagamento.OrdemServicoId, "execução falhou"));

        Assert.Null(_harness.Consumed.Select<EstornarPagamento>().Single().Exception);
        Assert.True(await _harness.Published.Any<PagamentoEstornado>());
        _gateway.Verify(g => g.EstornarAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        await using var db = Apoio.NovoDb(_dbName);
        Assert.Equal(StatusPagamento.Estornado, (await db.Pagamentos.SingleAsync()).Status);
    }

    [Fact]
    public async Task EstornarPagamento_nao_aprovado_ou_inexistente_falha_sem_chamar_gateway()
    {
        var pendente = new Pagamento(Guid.NewGuid(), Guid.NewGuid(), 150m);
        await Seed(pendente);

        await Consumir(new EstornarPagamento(pendente.OrdemServicoId, "x"));
        await Consumir(new EstornarPagamento(Guid.NewGuid(), "x"));

        Assert.All(_harness.Consumed.Select<EstornarPagamento>(), c => Assert.IsType<InvalidOperationException>(c.Exception));
        Assert.False(await _harness.Published.Any<PagamentoEstornado>());
        _gateway.Verify(g => g.EstornarAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
