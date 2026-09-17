using Pim.Api.Tiles;

namespace Pim.Api.Endpoints;

public static class TileEndpoints
{
    public static IEndpointRouteBuilder MapTileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/tiles/{z}/{x}/{y}.png", async (
            string z,
            string x,
            string y,
            TileService service,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (!TileCoordinate.TryParse(z, x, y, out var coordinate))
                return Results.BadRequest(new { message = "Invalid tile coordinates." });

            try
            {
                var tile = await service.GetTileAsync(coordinate, cancellationToken);
                httpContext.Response.Headers.CacheControl = "public, max-age=604800, immutable";
                httpContext.Response.Headers["X-PIM-Tile-Cache"] = tile.FromCache ? "HIT" : "MISS";
                return Results.File(tile.Bytes, "image/png");
            }
            catch (TileUpstreamException)
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
        }).AllowAnonymous();

        return endpoints;
    }
}
