using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using ModelContextProtocol.AspNetCore;

namespace S4MT.Mcp;

internal static class HttpEndpoints
{
    internal static void Map(WebApplication app, HostOptions options)
    {
        app.MapMcp("/mcp");
        app.MapGet("/api/cache/status", (GameIndexStore store) => Results.Ok(store.GetStatus()));
        app.MapPost("/api/cache/game-index", async (
            HttpRequest request,
            GameIndexStore store,
            CliProcessRunner runner,
            CancellationToken cancellationToken) =>
        {
            var sizeLimit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeLimit is { IsReadOnly: false })
                sizeLimit.MaxRequestBodySize = options.MaximumGameIndexBytes;

            if (request.ContentType is null ||
                !(request.ContentType.StartsWith("application/gzip", StringComparison.OrdinalIgnoreCase) ||
                  request.ContentType.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase)))
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            if (request.ContentLength is > 0 && request.ContentLength > options.MaximumGameIndexBytes)
                return Results.Json(new { error = new { code = "game_index_too_large", message = "The uploaded game index exceeds the configured size limit." } },
                    statusCode: StatusCodes.Status413PayloadTooLarge);

            using var importTimeout = new CancellationTokenSource(options.MaximumGameIndexImportMilliseconds);
            using var importCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, importTimeout.Token);
            try
            {
                var artifact = await store.ImportAsync(request.Body, request.ContentLength, runner, importCancellation.Token);
                return Results.Ok(new
                {
                    status = "active",
                    sha256 = artifact.Sha256,
                    gameVersion = artifact.GameVersion,
                    fingerprint = artifact.Fingerprint,
                    packageCount = artifact.PackageCount,
                    resourceCount = artifact.ResourceCount,
                    generatedUtc = artifact.GeneratedUtc,
                    activatedUtc = artifact.ActivatedUtc
                });
            }
            catch (OperationCanceledException) when (importTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return Results.Json(new { error = new { code = "game_index_import_timeout", message = "The game-index import exceeded maximumGameIndexImportMilliseconds." } },
                    statusCode: StatusCodes.Status408RequestTimeout);
            }
            catch (McpFailure failure)
            {
                var statusCode = failure.Code switch
                {
                    "game_index_too_large" => StatusCodes.Status413PayloadTooLarge,
                    "cache_storage_limit" => StatusCodes.Status507InsufficientStorage,
                    "cache_import_busy" => StatusCodes.Status409Conflict,
                    "invalid_game_index" => StatusCodes.Status400BadRequest,
                    _ => StatusCodes.Status500InternalServerError
                };
                return Results.Json(new { error = new { code = failure.Code, message = failure.Message } }, statusCode: statusCode);
            }
        });
    }
}
