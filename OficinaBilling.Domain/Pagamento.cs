namespace OficinaBilling.Domain;

public enum StatusPagamento { Pendente, Aprovado, Recusado, Estornado, Cancelado }

public class Pagamento
{
    private Pagamento() { } // EF

    public Pagamento(Guid orcamentoId, Guid ordemServicoId, decimal valor)
    {
        if (valor <= 0) throw new ArgumentException("Valor do pagamento deve ser maior que zero.", nameof(valor));

        Id = Guid.NewGuid();
        OrcamentoId = orcamentoId;
        OrdemServicoId = ordemServicoId;
        Valor = valor;
        Status = StatusPagamento.Pendente;
        CriadoEm = AtualizadoEm = DateTime.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid OrcamentoId { get; private set; }
    public Guid OrdemServicoId { get; private set; }
    public decimal Valor { get; private set; }
    public StatusPagamento Status { get; private set; }

    /// <summary>
    /// Id no gateway: preference id ao criar a cobrança; payment id do Mercado Pago após a confirmação.
    /// Nulo depois de aprovado = aprovado sem cobrança no gateway (simulação), então não há o que estornar lá.
    /// </summary>
    public string? GatewayId { get; private set; }
    public string? LinkPagamento { get; private set; }
    public DateTime CriadoEm { get; private set; }
    public DateTime AtualizadoEm { get; private set; }
    public string? Motivo { get; private set; }

    public void DefinirCobranca(string gatewayId, string? linkPagamento)
    {
        GarantirStatus(StatusPagamento.Pendente, "receber cobrança");
        GatewayId = gatewayId;
        LinkPagamento = linkPagamento;
        AtualizadoEm = DateTime.UtcNow;
    }

    public void Aprovar(string? gatewayPaymentId = null)
    {
        GarantirStatus(StatusPagamento.Pendente, "ser aprovado");
        // O preference id não serve para estorno (o refund exige o payment id), então não é mantido.
        GatewayId = string.IsNullOrWhiteSpace(gatewayPaymentId) ? null : gatewayPaymentId;
        Mudar(StatusPagamento.Aprovado, null);
    }

    public void Recusar(string motivo)
    {
        GarantirStatus(StatusPagamento.Pendente, "ser recusado");
        Mudar(StatusPagamento.Recusado, motivo);
    }

    public void Cancelar(string motivo)
    {
        GarantirStatus(StatusPagamento.Pendente, "ser cancelado");
        Mudar(StatusPagamento.Cancelado, motivo);
    }

    public void Estornar(string motivo)
    {
        GarantirStatus(StatusPagamento.Aprovado, "ser estornado");
        Mudar(StatusPagamento.Estornado, motivo);
    }

    private void GarantirStatus(StatusPagamento esperado, string acao)
    {
        if (Status != esperado)
            throw new InvalidOperationException($"Pagamento {Status} não pode {acao} (exige {esperado}).");
    }

    private void Mudar(StatusPagamento status, string? motivo)
    {
        Status = status;
        Motivo = motivo;
        AtualizadoEm = DateTime.UtcNow;
    }
}
