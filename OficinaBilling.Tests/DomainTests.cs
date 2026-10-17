using OficinaBilling.Domain;

namespace OficinaBilling.Tests;

public class OrcamentoTests
{
    [Fact]
    public void Novo_orcamento_fica_pendente_com_itens()
    {
        var o = Apoio.NovoOrcamento();

        Assert.Equal(StatusOrcamento.Pendente, o.Status);
        Assert.Equal(2, o.Itens.Count);
        Assert.NotEqual(Guid.Empty, o.Id);
    }

    [Fact]
    public void Nao_aceita_valor_zero_nem_sem_itens()
    {
        Assert.Throws<ArgumentException>(() => Apoio.NovoOrcamento(valor: 0));
        Assert.Throws<ArgumentException>(() => new Orcamento(Guid.NewGuid(), "OS", Guid.NewGuid(), null, [], 10m));
    }

    [Fact]
    public void Aprovar_somente_a_partir_de_pendente()
    {
        var o = Apoio.NovoOrcamento();
        o.Aprovar();
        Assert.Equal(StatusOrcamento.Aprovado, o.Status);
        Assert.Throws<InvalidOperationException>(o.Aprovar);
    }

    [Fact]
    public void Cancelar_registra_motivo_e_nao_cancela_duas_vezes()
    {
        var o = Apoio.NovoOrcamento();
        o.Aprovar();
        o.Cancelar("pagamento recusado");

        Assert.Equal(StatusOrcamento.Cancelado, o.Status);
        Assert.Equal("pagamento recusado", o.MotivoCancelamento);
        Assert.Throws<InvalidOperationException>(() => o.Cancelar("de novo"));
        Assert.Throws<InvalidOperationException>(o.Aprovar);
    }
}

public class PagamentoTests
{
    private static Pagamento Novo() => new(Guid.NewGuid(), Guid.NewGuid(), 150m);

    [Fact]
    public void Novo_pagamento_fica_pendente_e_recebe_cobranca()
    {
        var p = Novo();
        p.DefinirCobranca("pref-1", "https://link");

        Assert.Equal(StatusPagamento.Pendente, p.Status);
        Assert.Equal("pref-1", p.GatewayId);
        Assert.Equal("https://link", p.LinkPagamento);
        Assert.Throws<ArgumentException>(() => new Pagamento(Guid.NewGuid(), Guid.NewGuid(), 0));
    }

    [Fact]
    public void Aprovar_troca_gateway_id_pelo_payment_id()
    {
        var p = Novo();
        p.DefinirCobranca("pref-1", null);
        p.Aprovar("123");

        Assert.Equal(StatusPagamento.Aprovado, p.Status);
        Assert.Equal("123", p.GatewayId);
        Assert.Throws<InvalidOperationException>(() => p.Aprovar());
        Assert.Throws<InvalidOperationException>(() => p.DefinirCobranca("x", null));
    }

    [Fact]
    public void Aprovar_sem_payment_id_mantem_gateway_id()
    {
        var p = Novo();
        p.DefinirCobranca("pref-1", null);
        p.Aprovar();
        Assert.Equal("pref-1", p.GatewayId);
    }

    [Fact]
    public void Recusar_e_cancelar_somente_pendente()
    {
        var recusado = Novo();
        recusado.Recusar("cartão recusado");
        Assert.Equal(StatusPagamento.Recusado, recusado.Status);
        Assert.Equal("cartão recusado", recusado.Motivo);
        Assert.Throws<InvalidOperationException>(() => recusado.Cancelar("x"));

        var cancelado = Novo();
        cancelado.Cancelar("orçamento reprovado");
        Assert.Equal(StatusPagamento.Cancelado, cancelado.Status);
        Assert.Throws<InvalidOperationException>(() => cancelado.Recusar("x"));
    }

    [Fact]
    public void Estornar_somente_aprovado()
    {
        var p = Novo();
        Assert.Throws<InvalidOperationException>(() => p.Estornar("x"));

        p.Aprovar();
        p.Estornar("execução falhou");
        Assert.Equal(StatusPagamento.Estornado, p.Status);
        Assert.Throws<InvalidOperationException>(() => p.Estornar("x"));
    }
}
