using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;

namespace S4MT.Mcp;

internal static class ToolResult
{
    internal static async Task<CallToolResult> Execute(
        IHostApplicationLifetime lifetime,
        Func<CancellationToken, Task<CliReport>> operation,
        CancellationToken cancellationToken,
        Func<GameIndexArtifact?>? gameIndexProvenance = null)
    {
        try
        {
            CliProcessRunner.EnsureSupportedPlatform(OperatingSystem.IsWindows(), OperatingSystem.IsLinux());
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
            linked.Token.ThrowIfCancellationRequested();
            var result = await operation(linked.Token);
            var report = result.Report;
            var command = report.GetProperty("command").GetString();
            var status = report.TryGetProperty("valid", out var valid) && valid.ValueKind == JsonValueKind.False ? "invalid" : "completed";
            var diagnostics = report.TryGetProperty("diagnostics", out var items) && items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 0;
            var summary = $"{command} {status}. {diagnostics} diagnostics. CLI exit {result.ExitCode}.";
            if (!string.IsNullOrEmpty(result.Stderr)) summary += " CLI stderr is available in _meta.cliStderr.";
            var metadata = new JsonObject { ["cliExitCode"] = result.ExitCode, ["cliStderr"] = result.Stderr };
            if (gameIndexProvenance?.Invoke() is { } artifact)
            {
                metadata["gameIndexSha256"] = artifact.Sha256;
                metadata["gameIndexGameVersion"] = artifact.GameVersion;
                metadata["gameIndexFingerprint"] = artifact.Fingerprint;
            }
            return new CallToolResult
            {
                StructuredContent = report,
                Content = [new TextContentBlock { Text = summary }],
                Meta = metadata
            };
        }
        catch (OperationCanceledException)
        {
            return Error(new McpFailure("cancelled", "Invocation cancelled before the CLI started."));
        }
        catch (McpFailure exception) { return Error(exception); }
        catch (S4MT.Cli.CliInputException exception) { return Error(new McpFailure("unsafe_project_path", exception.Message)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            return Error(new McpFailure("file_access_error", exception.Message));
        }
    }

    internal static CallToolResult Error(McpFailure failure) => new()
    {
        IsError = true,
        StructuredContent = JsonSerializer.SerializeToElement(new { error = new { code = failure.Code, message = failure.Message, exitCode = failure.ExitCode, stderr = failure.Stderr } }),
        Content = [new TextContentBlock { Text = $"{failure.Code}: {failure.Message}" }]
    };
}
