using MassTransit;
using Microsoft.EntityFrameworkCore;
using OficinaBilling.Domain;

namespace OficinaBilling.Api.Persistencia;

public class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options)
{
    public DbSet<Orcamento> Orcamentos => Set<Orcamento>();
    public DbSet<Pagamento> Pagamentos => Set<Pagamento>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Orcamento>(e =>
        {
            e.ToTable("orcamentos");
            e.HasKey(o => o.Id);
            e.HasIndex(o => o.OrdemServicoId).IsUnique();
            e.Property(o => o.NumeroOs).HasMaxLength(50).IsRequired();
            e.Property(o => o.EmailCliente).HasMaxLength(200);
            e.Property(o => o.Valor).HasPrecision(18, 2);
            e.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(o => o.MotivoCancelamento).HasMaxLength(500);
            e.OwnsMany(o => o.Itens, i =>
            {
                i.ToTable("orcamento_itens");
                i.WithOwner().HasForeignKey("OrcamentoId");
                i.Property<int>("Id");
                i.HasKey("Id");
                i.Property(x => x.Descricao).HasMaxLength(200).IsRequired();
                i.Property(x => x.Tipo).HasMaxLength(20).IsRequired();
                i.Property(x => x.PrecoUnitario).HasPrecision(18, 2);
            });
            e.Navigation(o => o.Itens).HasField("_itens").UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<Pagamento>(e =>
        {
            e.ToTable("pagamentos");
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.OrdemServicoId).IsUnique();
            e.Property(p => p.Valor).HasPrecision(18, 2);
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.GatewayId).HasMaxLength(100);
            e.Property(p => p.LinkPagamento).HasMaxLength(500);
            e.Property(p => p.Motivo).HasMaxLength(500);
        });

        // Tabelas do inbox/outbox do MassTransit: gravar o estado e publicar o evento na mesma transação.
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
