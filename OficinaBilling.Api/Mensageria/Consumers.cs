using MassTransit;
using Microsoft.EntityFrameworkCore;
using OficinaBilling.Api.Pagamentos;
using OficinaBilling.Api.Persistencia;
using OficinaBilling.Domain;
using OficinaMecanica.Contracts;

namespace OficinaBilling.Api.Mensageria;

// Consumers da Saga. Todos são idempotentes por OrdemServicoId: o broker entrega "at least once",
// então uma reentrega republica o mesmo evento em vez de duplicar estado.
// Em produção o outbox do MassTransit (EF) grava estado + eventos na mesma transação.

public class GerarOrcamentoConsumer(BillingDbContext db) : IConsumer<GerarOrcamento>
{
    public async Task Consume(ConsumeContext<GerarOrcamento> context)
    {
        var m = context.Message;

        var existente = await db.Orcamentos.FirstOrDefaultAsync(o => o.OrdemServicoId == m.OrdemServicoId, context.CancellationToken);
        if (existente is not null)
        {
            await context.Publish(new OrcamentoGerado(m.OrdemServicoId, existente.Id, existente.Valor));
            return;
        }

        if (m.Valor <= 0 || m.Itens is null || m.Itens.Count == 0)
        {
            await context.Publish(new OrcamentoFalhou(m.OrdemServicoId, "Orçamento precisa ter itens e valor maior que zero."));
            return;
        }

        var orcamento = new Orcamento(m.OrdemServicoId, m.NumeroOs, m.ClienteId, m.EmailCliente,
            m.Itens.Select(i => new OrcamentoItem(i.Descricao, i.Tipo, i.Quantidade, i.PrecoUnitario)), m.Valor);
        db.Orcamentos.Add(orcamento);

        await context.Publish(new OrcamentoGerado(m.OrdemServicoId, orcamento.Id, orcamento.Valor));
        await db.SaveChangesAsync(context.CancellationToken);
    }
}

public class AprovarOrcamentoConsumer(BillingDbContext db, IGatewayPagamento gateway, ILogger<AprovarOrcamentoConsumer> logger)
    : IConsumer<AprovarOrcamento>
{
    public async Task Consume(ConsumeContext<AprovarOrcamento> context)
    {
        var osId = context.Message.OrdemServicoId;

        var pagamento = await db.Pagamentos.FirstOrDefaultAsync(p => p.OrdemServicoId == osId, context.CancellationToken);
        if (pagamento is not null)
        {
            await context.Publish(new PagamentoCriado(osId, pagamento.Id, pagamento.LinkPagamento));
            return;
        }

        var orcamento = await db.Orcamentos.FirstOrDefaultAsync(o => o.OrdemServicoId == osId, context.CancellationToken)
            ?? throw new InvalidOperationException($"Orçamento da OS {osId} não encontrado.");

        if (orcamento.Status == StatusOrcamento.Cancelado)
        {
            logger.LogWarning("AprovarOrcamento ignorado: orçamento da OS {OrdemServicoId} está cancelado", osId);
            return;
        }

        orcamento.Aprovar();
        pagamento = new Pagamento(orcamento.Id, osId, orcamento.Valor);
        var cobranca = await gateway.CriarCobrancaAsync(pagamento, orcamento, context.CancellationToken);
        pagamento.DefinirCobranca(cobranca.GatewayId, cobranca.LinkPagamento);
        db.Pagamentos.Add(pagamento);

        await context.Publish(new PagamentoCriado(osId, pagamento.Id, pagamento.LinkPagamento));
        await db.SaveChangesAsync(context.CancellationToken);
    }
}

/// <summary>Compensação: cancela o orçamento e o pagamento ainda pendente (não há evento de resposta no contrato).</summary>
public class CancelarOrcamentoConsumer(BillingDbContext db) : IConsumer<CancelarOrcamento>
{
    public async Task Consume(ConsumeContext<CancelarOrcamento> context)
    {
        var m = context.Message;

        var orcamento = await db.Orcamentos.FirstOrDefaultAsync(o => o.OrdemServicoId == m.OrdemServicoId, context.CancellationToken);
        if (orcamento is not null && orcamento.Status != StatusOrcamento.Cancelado)
            orcamento.Cancelar(m.Motivo);

        var pagamento = await db.Pagamentos.FirstOrDefaultAsync(p => p.OrdemServicoId == m.OrdemServicoId, context.CancellationToken);
        if (pagamento?.Status == StatusPagamento.Pendente)
            pagamento.Cancelar(m.Motivo);

        await db.SaveChangesAsync(context.CancellationToken);
    }
}

/// <summary>Compensação: estorna no gateway o pagamento aprovado e avisa o orquestrador.</summary>
public class EstornarPagamentoConsumer(BillingDbContext db, IGatewayPagamento gateway) : IConsumer<EstornarPagamento>
{
    public async Task Consume(ConsumeContext<EstornarPagamento> context)
    {
        var m = context.Message;

        var pagamento = await db.Pagamentos.FirstOrDefaultAsync(p => p.OrdemServicoId == m.OrdemServicoId, context.CancellationToken)
            ?? throw new InvalidOperationException($"Pagamento da OS {m.OrdemServicoId} não encontrado.");

        if (pagamento.Status != StatusPagamento.Estornado)
        {
            pagamento.Estornar(m.Motivo); // valida a transição (só Aprovado) antes de chamar o gateway
            // Sem payment id = aprovado por simulação, nada foi cobrado no gateway.
            if (pagamento.GatewayId is not null)
                await gateway.EstornarAsync(pagamento.GatewayId, context.CancellationToken);
            BillingMetrics.PagamentosEstornados.Add(1);
        }

        await context.Publish(new PagamentoEstornado(m.OrdemServicoId, pagamento.Id));
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
