using System.Text.Json;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using OficinaBilling.Api.Pagamentos;
using OficinaBilling.Api.Persistencia;
using OficinaBilling.Domain;
using OficinaMecanica.Contracts;

namespace OficinaBilling.Api.Controllers;

public record SimularPagamentoRequest(bool Aprovado, string? Motivo);

/// <summary>Notificação do Mercado Pago (webhook). data.id pode vir como string ou número.</summary>
public record NotificacaoMercadoPago(string? Type, string? Action, DadosNotificacao? Data);
public record DadosNotificacao(JsonElement Id);

[ApiController]
[Route("api/pagamentos")]
public class PagamentosController(
    BillingDbContext db,
    IPublishEndpoint publish,
    IGatewayPagamento gateway,
    ILogger<PagamentosController> logger) : ControllerBase
{
    /// <summary>Pagamento da OS. Cliente só enxerga o próprio (sub do token == ClienteId do orçamento).</summary>
    [HttpGet("{ordemServicoId:guid}")]
    [Authorize(Roles = Perfis.StaffOuCliente)]
    [ProducesResponseType<Pagamento>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obter(Guid ordemServicoId, CancellationToken ct)
    {
        var pagamento = await db.Pagamentos.AsNoTracking().FirstOrDefaultAsync(p => p.OrdemServicoId == ordemServicoId, ct);
        if (pagamento is null) return NotFound();

        var clienteId = await db.Orcamentos.Where(o => o.Id == pagamento.OrcamentoId).Select(o => o.ClienteId).FirstOrDefaultAsync(ct);
        if (!Perfis.PodeVer(User, clienteId)) return Forbid();
        return Ok(pagamento);
    }

    /// <summary>
    /// Resolve manualmente o pagamento pendente (gateway fake / demo): aprovado → PagamentoAprovado,
    /// recusado → PagamentoRecusado.
    /// </summary>
    [HttpPost("{ordemServicoId:guid}/simular")]
    [Authorize(Roles = "Administrador,Recepcionista")]
    [ProducesResponseType<Pagamento>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Simular(Guid ordemServicoId, SimularPagamentoRequest request, CancellationToken ct)
    {
        var pagamento = await db.Pagamentos.FirstOrDefaultAsync(p => p.OrdemServicoId == ordemServicoId, ct);
        if (pagamento is null) return NotFound();
        if (pagamento.Status != StatusPagamento.Pendente)
            return Conflict(new { mensagem = $"Pagamento já está {pagamento.Status}." });

        await Resolver(pagamento, request.Aprovado, request.Motivo ?? "Pagamento recusado (simulação).", null, ct);
        return Ok(pagamento);
    }

    /// <summary>
    /// Webhook do Mercado Pago. O payload nunca é confiado: o pagamento é consultado na API do MP
    /// e casado pelo external_reference (= PagamentoId). Sempre responde 200 para o MP não reenviar à toa.
    /// </summary>
    [HttpPost("webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> Webhook(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] NotificacaoMercadoPago? notificacao,
        [FromQuery(Name = "type")] string? tipoQuery,
        [FromQuery(Name = "data.id")] string? idQuery,
        CancellationToken ct)
    {
        if (gateway is GatewayPagamentoFake) return Ok();

        var tipo = notificacao?.Type ?? tipoQuery;
        var dataId = notificacao?.Data?.Id;
        var paymentId = dataId?.ValueKind is JsonValueKind.String or JsonValueKind.Number ? dataId.Value.ToString() : idQuery;
        if (tipo != "payment" || string.IsNullOrWhiteSpace(paymentId)) return Ok();

        var noGateway = await gateway.ConsultarPagamentoAsync(paymentId, ct);
        if (noGateway is null || !Guid.TryParse(noGateway.ExternalReference, out var pagamentoId)) return Ok();

        var pagamento = await db.Pagamentos.FirstOrDefaultAsync(p => p.Id == pagamentoId, ct);
        if (pagamento is null || pagamento.Status != StatusPagamento.Pendente) return Ok(); // já processado

        switch (noGateway.Status)
        {
            case "approved":
                await Resolver(pagamento, true, null, noGateway.Id, ct);
                break;
            case "rejected" or "cancelled":
                await Resolver(pagamento, false, $"Mercado Pago: {noGateway.Status} ({noGateway.StatusDetalhe})", noGateway.Id, ct);
                break;
            default:
                logger.LogInformation("Pagamento {PaymentId} ainda {Status} no Mercado Pago", noGateway.Id, noGateway.Status);
                break;
        }
        return Ok();
    }

    private async Task Resolver(Pagamento pagamento, bool aprovado, string? motivo, string? gatewayPaymentId, CancellationToken ct)
    {
        if (aprovado)
        {
            pagamento.Aprovar(gatewayPaymentId);
            await publish.Publish(new PagamentoAprovado(pagamento.OrdemServicoId, pagamento.Id, pagamento.Valor), ct);
            BillingMetrics.PagamentosAprovados.Add(1);
        }
        else
        {
            pagamento.Recusar(motivo!);
            await publish.Publish(new PagamentoRecusado(pagamento.OrdemServicoId, pagamento.Id, motivo!), ct);
            BillingMetrics.PagamentosRecusados.Add(1);
        }
        await db.SaveChangesAsync(ct);
    }
}
