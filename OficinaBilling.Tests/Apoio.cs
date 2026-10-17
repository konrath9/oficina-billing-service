using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OficinaBilling.Api.Mensageria;
using OficinaBilling.Api.Pagamentos;
using OficinaBilling.Api.Persistencia;
using OficinaBilling.Domain;

namespace OficinaBilling.Tests;

internal static class Apoio
{
    public const string JwtSecret = "OficinaMecanica@SecretKey#2024!MustBe32CharsMin";

    public static BillingDbContext NovoDb(string? nome = null) =>
        new(new DbContextOptionsBuilder<BillingDbContext>().UseInMemoryDatabase(nome ?? Guid.NewGuid().ToString()).Options);

    public static Orcamento NovoOrcamento(Guid? osId = null, Guid? clienteId = null, decimal valor = 150m) =>
        new(osId ?? Guid.NewGuid(), "OS-0001", clienteId ?? Guid.NewGuid(), "cliente@teste.com",
            [new OrcamentoItem("Troca de óleo", "Servico", 1, 100m), new OrcamentoItem("Filtro", "Peca", 2, 25m)], valor);

    /// <summary>Provider com DbContext InMemory, gateway informado e test harness do MassTransit com todos os consumers.</summary>
    public static async Task<(ServiceProvider Provider, ITestHarness Harness)> NovoHarness(IGatewayPagamento gateway, string dbName)
    {
        var provider = new ServiceCollection()
            .AddLogging()
            .AddDbContext<BillingDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddSingleton(gateway)
            .AddMassTransitTestHarness(x => x.AddConsumers(typeof(GerarOrcamentoConsumer).Assembly))
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        return (provider, harness);
    }

    public static ClaimsPrincipal Usuario(string role, Guid? sub = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, role) };
        if (sub is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, sub.Value.ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    public static string Token(string role, Guid? sub = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, (sub ?? Guid.NewGuid()).ToString()),
            new(ClaimTypes.Role, role)
        };
        var credenciais = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSecret)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken("OficinaMecanicaAPI", "OficinaMecanicaClients", claims,
            expires: DateTime.UtcNow.AddHours(1), signingCredentials: credenciais);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
