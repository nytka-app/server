using Npgsql;

namespace Nytka.Server.Api;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", async (NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            try
            {
                await using var command = dataSource.CreateCommand("select 1");
                await command.ExecuteScalarAsync(ct);
                return Results.Ok(new { status = "healthy" });
            }
            catch (NpgsqlException)
            {
                return Results.Json(new { status = "unhealthy" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        return app;
    }
}
