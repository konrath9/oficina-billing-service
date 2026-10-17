using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OficinaBilling.Api.Persistencia;
using OficinaBilling.Domain;

namespace OficinaBilling.Api.Controllers;

[ApiController]
[Route("api/orcamentos")]
[Authorize(Roles = Perfis.StaffOuCliente)]
public class OrcamentosController(BillingDbContext db) : ControllerBase
{
    /// <summary>Orçamento da OS. Cliente só enxerga o próprio (sub do token == ClienteId).</summary>
    [HttpGet("{ordemServicoId:guid}")]
    [ProducesResponseType<Orcamento>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obter(Guid ordemServicoId, CancellationToken ct)
    {
        var orcamento = await db.Orcamentos.AsNoTracking().FirstOrDefaultAsync(o => o.OrdemServicoId == ordemServicoId, ct);
        if (orcamento is null) return NotFound();
        if (!Perfis.PodeVer(User, orcamento.ClienteId)) return Forbid();
        return Ok(orcamento);
    }
}

public static class Perfis
{
    public const string Staff = "Administrador,Mecanico,Recepcionista";
    public const string StaffOuCliente = Staff + ",Cliente";
    public const string Cliente = "Cliente";

    /// <summary>Staff vê tudo; token de Cliente só vê recursos do próprio ClienteId (claim sub).</summary>
    public static bool PodeVer(ClaimsPrincipal user, Guid clienteId)
    {
        if (!user.IsInRole(Cliente)) return true;
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) && id == clienteId;
    }
}
