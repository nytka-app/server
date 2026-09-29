using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Nytka.Server.Auth;

/// <summary>Accepts <c>Authorization: Bearer &lt;admin token&gt;</c>, compared in constant time.</summary>
public sealed class BearerTokenFilter(IOptions<NytkaOptions> options) : IEndpointFilter
{
    private const string Scheme = "Bearer ";

    private readonly byte[] _expected = Encoding.UTF8.GetBytes(options.Value.AdminToken);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var header = context.HttpContext.Request.Headers.Authorization.ToString();
        if (header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header[Scheme.Length..].Trim()), _expected))
        {
            return await next(context);
        }

        context.HttpContext.Response.Headers.WWWAuthenticate = "Bearer";
        return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Missing or wrong token.");
    }
}
