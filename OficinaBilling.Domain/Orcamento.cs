namespace OficinaBilling.Domain;

public enum StatusOrcamento { Pendente, Aprovado, Cancelado }

/// <summary>Item do orçamento (serviço ou peça), copiado da OS no momento da geração.</summary>
public class OrcamentoItem
{
    private OrcamentoItem() { Descricao = Tipo = string.Empty; } // EF

    public OrcamentoItem(string descricao, string tipo, int quantidade, decimal precoUnitario)
    {
        Descricao = descricao;
        Tipo = tipo;
        Quantidade = quantidade;
        PrecoUnitario = precoUnitario;
    }

    public string Descricao { get; private set; }
    public string Tipo { get; private set; } // "Servico" | "Peca"
    public int Quantidade { get; private set; }
    public decimal PrecoUnitario { get; private set; }
}

public class Orcamento
{
    private readonly List<OrcamentoItem> _itens = new();

    private Orcamento() { NumeroOs = string.Empty; } // EF

    public Orcamento(Guid ordemServicoId, string numeroOs, Guid clienteId, string? emailCliente,
        IEnumerable<OrcamentoItem> itens, decimal valor)
    {
        _itens.AddRange(itens);
        if (_itens.Count == 0) throw new ArgumentException("Orçamento precisa ter ao menos um item.", nameof(itens));
        if (valor <= 0) throw new ArgumentException("Valor do orçamento deve ser maior que zero.", nameof(valor));

        Id = Guid.NewGuid();
        OrdemServicoId = ordemServicoId;
        NumeroOs = numeroOs;
        ClienteId = clienteId;
        EmailCliente = emailCliente;
        Valor = valor;
        Status = StatusOrcamento.Pendente;
        CriadoEm = AtualizadoEm = DateTime.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid OrdemServicoId { get; private set; }
    public string NumeroOs { get; private set; }
    public Guid ClienteId { get; private set; }
    public string? EmailCliente { get; private set; }
    public IReadOnlyCollection<OrcamentoItem> Itens => _itens;
    public decimal Valor { get; private set; }
    public StatusOrcamento Status { get; private set; }
    public DateTime CriadoEm { get; private set; }
    public DateTime AtualizadoEm { get; private set; }
    public string? MotivoCancelamento { get; private set; }

    public void Aprovar()
    {
        if (Status != StatusOrcamento.Pendente)
            throw new InvalidOperationException($"Orçamento {Status} não pode ser aprovado.");
        Status = StatusOrcamento.Aprovado;
        AtualizadoEm = DateTime.UtcNow;
    }

    public void Cancelar(string motivo)
    {
        if (Status == StatusOrcamento.Cancelado)
            throw new InvalidOperationException("Orçamento já está cancelado.");
        Status = StatusOrcamento.Cancelado;
        MotivoCancelamento = motivo;
        AtualizadoEm = DateTime.UtcNow;
    }
}
