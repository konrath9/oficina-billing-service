// Contratos de mensageria da Saga — copia fiel de docs/fase4/contratos-mensageria.md (repo OficinaMecanica).
// O namespace e os nomes de tipos/propriedades precisam ser idênticos nos três serviços.
namespace OficinaMecanica.Contracts;

// ---------- OS Service -> Billing Service (comandos) ----------
public record ItemOrcamento(string Descricao, string Tipo, int Quantidade, decimal PrecoUnitario); // Tipo: "Servico" | "Peca"

public record GerarOrcamento(Guid OrdemServicoId, string NumeroOs, Guid ClienteId, string? EmailCliente,
    IReadOnlyList<ItemOrcamento> Itens, decimal Valor);
public record AprovarOrcamento(Guid OrdemServicoId);                 // cliente aprovou -> Billing cria a cobrança
public record CancelarOrcamento(Guid OrdemServicoId, string Motivo); // compensação
public record EstornarPagamento(Guid OrdemServicoId, string Motivo); // compensação

// ---------- Billing Service -> OS Service (eventos) ----------
public record OrcamentoGerado(Guid OrdemServicoId, Guid OrcamentoId, decimal Valor);
public record OrcamentoFalhou(Guid OrdemServicoId, string Motivo);
public record PagamentoCriado(Guid OrdemServicoId, Guid PagamentoId, string? LinkPagamento);
public record PagamentoAprovado(Guid OrdemServicoId, Guid PagamentoId, decimal Valor);
public record PagamentoRecusado(Guid OrdemServicoId, Guid PagamentoId, string Motivo);
public record PagamentoEstornado(Guid OrdemServicoId, Guid PagamentoId);

// ---------- OS Service -> Execution Service (comandos) ----------
public record ServicoExecucao(Guid ServicoId, string Descricao, int Quantidade);

public record IniciarExecucao(Guid OrdemServicoId, string NumeroOs, IReadOnlyList<ServicoExecucao> Servicos);
public record CancelarExecucao(Guid OrdemServicoId, string Motivo); // compensação

// ---------- Execution Service -> OS Service (eventos) ----------
public record ExecucaoIniciada(Guid OrdemServicoId);                       // OS entrou na fila de execução
public record ExecucaoFinalizada(Guid OrdemServicoId, DateTime FinalizadaEm);
public record ExecucaoFalhou(Guid OrdemServicoId, string Motivo);
