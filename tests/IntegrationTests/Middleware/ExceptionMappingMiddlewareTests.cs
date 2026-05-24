using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.Api;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Middleware;

// Exercises ExceptionMappingMiddleware in isolation: a minimal TestServer pipeline
// of the middleware wrapping a terminal that throws the exception under test. No
// database, no command bus, no DI graph. This is where the ValidationException
// mapping is proved, since no IValidator is registered on disk to raise one through
// a real command.
public class ExceptionMappingMiddlewareTests
{
    private static async Task<HttpClient> BuildClientAsync(RequestDelegate terminal)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .Configure(app =>
                {
                    app.UseMiddleware<ExceptionMappingMiddleware>();
                    app.Run(terminal);
                }))
            .StartAsync();
        return host.GetTestClient();
    }

    [Fact]
    public async Task ValidationException_maps_to_400_problem_details()
    {
        var client = await BuildClientAsync(_ => throw new ValidationException(
            [new ValidationError("Sku", "must be non-empty")]));

        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errors").GetProperty("Sku").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("must be non-empty");
    }

    [Fact]
    public async Task DomainException_maps_to_422()
    {
        var client = await BuildClientAsync(_ => throw new DomainException("rule broken"));

        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("RULE_VIOLATED");
        body.GetProperty("message").GetString().Should().Be("rule broken");
    }

    [Fact]
    public async Task ConcurrencyException_maps_to_409_with_expected_version()
    {
        var streamId = StreamId.Parse($"order:{Guid.NewGuid():N}");
        var client = await BuildClientAsync(_ => throw new ConcurrencyException(streamId, 3));

        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("CONCURRENCY");
        body.GetProperty("expectedVersion").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task AggregateNotFoundException_maps_to_404_with_aggregate_id()
    {
        var aggregateId = Guid.NewGuid();
        var client = await BuildClientAsync(_ => throw new AggregateNotFoundException(aggregateId));

        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("NOT_FOUND");
        body.GetProperty("aggregateId").GetGuid().Should().Be(aggregateId);
    }

    [Fact]
    public async Task An_unhandled_exception_maps_to_500_without_leaking_details()
    {
        var client = await BuildClientAsync(_ => throw new InvalidOperationException("secret detail"));

        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain("secret detail");
        var body = JsonSerializer.Deserialize<JsonElement>(raw);
        body.GetProperty("code").GetString().Should().Be("INTERNAL");
    }

    [Fact]
    public async Task A_successful_request_passes_through_unchanged()
    {
        var client = await BuildClientAsync(async ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            await ctx.Response.WriteAsync("ok");
        });

        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("ok");
    }
}
