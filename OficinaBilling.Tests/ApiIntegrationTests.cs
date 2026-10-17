using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MassTransit;
using MassTransit.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OficinaBilling.Api.Persistencia;
using OficinaBilling.Domain;
using OficinaMecanica.Contracts;

namespace OficinaBilling.Tests;

/// <summary>Sobe a API inteira (JWT, Swagger, health, middleware) com banco InMemory e MassTransit em memória.</summary>
public class BillingApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<BillingDbContext>>();
            services.AddDbContext<BillingDbContext>(o => o.UseInMemoryDatabase(_dbName));
            services.AddMassTransitTestHarness();
        });
    }
}

internal static class ServiceCollectionTestExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        foreach (var d in services.Where(d => d.ServiceType == typeof(T)).ToList()) services.Remove(d);
    }
}

public class ApiIntegrationTests(BillingApiFactory factory) : IClassFixture<BillingApiFactory>
{
    private HttpClient Cliente(string? role = null, Guid? sub = null)
    {
        var client = factory.CreateClient();
        if (role is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Apoio.Token(role, sub));
        return client;
    }

    private async Task<Pagamento> SeedPagamento(Guid clienteId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var orcamento = Apoio.NovoOrcamento(clienteId: clienteId);
        orcamento.Aprovar();
        var pagamento = new Pagamento(orcamento.Id, orcamento.OrdemServicoId, orcamento.Valor);
        db.AddRange(orcamento, pagamento);
        await db.SaveChangesAsync();
        return pagamento;
    }

    [Fact]
    public async Task Swagger_e_health_respondem_com_correlation_id()
    {
        var client = Cliente();
        var swagger = await client.GetAsync("/billing/swagger/v1/swagger.json");
        var health = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, swagger.StatusCode);
        Assert.Contains("/api/pagamentos/webhook", await swagger.Content.ReadAsStringAsync());
        Assert.True(health.Headers.Contains("X-Correlation-Id"));
    }

    [Fact]
    public async Task Endpoints_exigem_token_e_role()
    {
        var osId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Cliente().GetAsync($"/api/orcamentos/{osId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Cliente("Mecanico").GetAsync($"/api/pagamentos/{osId}")).StatusCode);

        var simularMecanico = await Cliente("Mecanico").PostAsJsonAsync($"/api/pagamentos/{osId}/simular", new { aprovado = true });
        Assert.Equal(HttpStatusCode.Forbidden, simularMecanico.StatusCode);
    }

    [Fact]
    public async Task Cliente_ve_o_proprio_pagamento_e_recepcionista_simula_aprovacao()
    {
        var clienteId = Guid.NewGuid();
        var pagamento = await SeedPagamento(clienteId);

        var proprio = await Cliente("Cliente", clienteId).GetAsync($"/api/pagamentos/{pagamento.OrdemServicoId}");
        var alheio = await Cliente("Cliente", Guid.NewGuid()).GetAsync($"/api/orcamentos/{pagamento.OrdemServicoId}");
        Assert.Equal(HttpStatusCode.OK, proprio.StatusCode);
        Assert.Contains("\"Pendente\"", await proprio.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, alheio.StatusCode);

        var simular = await Cliente("Recepcionista").PostAsJsonAsync($"/api/pagamentos/{pagamento.OrdemServicoId}/simular", new { aprovado = true });
        Assert.Equal(HttpStatusCode.OK, simular.StatusCode);

        var harness = factory.Services.GetRequiredService<ITestHarness>();
        Assert.True(await harness.Published.Any<PagamentoAprovado>(x => x.Context.Message.PagamentoId == pagamento.Id));
    }

    [Fact]
    public async Task Webhook_e_anonimo_e_ignorado_com_gateway_fake()
    {
        var resposta = await Cliente().PostAsJsonAsync("/api/pagamentos/webhook", new { type = "payment", data = new { id = "123" } });
        var semCorpo = await Cliente().PostAsync("/api/pagamentos/webhook?type=payment&data.id=123", null);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(HttpStatusCode.OK, semCorpo.StatusCode);
    }
}
