using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace S4MT.Mcp;

[McpServerToolType]
internal sealed class ProjectTools(PathPolicy paths, CliProcessRunner runner, IHostApplicationLifetime lifetime)
{
    [McpServerTool(Name = "s4mt_project_init", Destructive = false)]
    [Description("Create or repair an S4MT project scaffold in an explicit writable directory. Existing project names must match. Does not install or launch anything.")]
    public Task<CallToolResult> Init(
        [Description("Absolute project directory under a configured write root. Spaces in directory paths are supported.")] string path,
        [Description("Package identifier, 1 to 64 ASCII letters, digits, dots, underscores or hyphens. First character must be a letter or digit.")] string name,
        CancellationToken cancellationToken) => Execute(async token =>
    {
        var project = paths.Write(path);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || !char.IsAsciiLetterOrDigit(name[0]) ||
            name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
            throw new McpFailure("invalid_arguments", "Use a package-safe project name with 1 to 64 characters. Directory paths may contain spaces.");
        paths.ProjectInputs(project, initialize: true);
        return await runner.RunAsync(["project", "init", "--path", project, "--name", name], "project.init", token);
    }, cancellationToken);

    [McpServerTool(Name = "s4mt_project_validate", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Validate an S4MT project directory or explicit project JSON file. Returns CLI diagnostics, warnings and partial validation coverage unchanged. An invalid project is a structured valid:false report.")]
    public Task<CallToolResult> Validate(
        [Description("Absolute project directory or project JSON file under configured read roots.")] string project,
        CancellationToken cancellationToken,
        [Description("Optional absolute read-only game index file under configured read or game roots.")] string? gameIndex = null) => Execute(async token =>
    {
        var input = paths.Read(project);
        paths.ProjectInputs(input);
        var arguments = new List<string> { "project", "validate", "--project", input };
        AddGameIndex(arguments, gameIndex);
        return await runner.RunAsync(arguments, "project.validate", token);
    }, cancellationToken);

    [McpServerTool(Name = "s4mt_project_build", Destructive = false, Idempotent = true)]
    [Description("Build and read back an S4MT package and script bundle. Requires an empty or MCP-owned dedicated output root. Repeated builds reuse verified fingerprints. Package verification does not prove in-game behavior.")]
    public Task<CallToolResult> Build(
        [Description("Absolute project directory or project JSON file under configured read roots.")] string project,
        [Description("Absolute dedicated build directory under a write root. Must be empty on first use. Game roots are read-only.")] string outputRoot,
        CancellationToken cancellationToken,
        [Description("Optional absolute read-only game index file.")] string? gameIndex = null) => Execute(async token =>
    {
        var input = paths.Read(project);
        var output = paths.Write(outputRoot);
        var inputs = paths.ProjectInputs(input);
        var arguments = new List<string> { "project", "build", "--project", input, "--output-root", output };
        AddGameIndex(arguments, gameIndex);
        paths.NoCollision(output, gameIndex is null ? inputs : inputs.Append(paths.Read(gameIndex)));
        using var lease = BuildOutputLease.Acquire(output, paths);
        try
        {
            var result = await runner.RunAsync(arguments, "project.build", token, lease.ChildStarted);
            lease.Complete();
            return result;
        }
        catch (McpFailure exception)
        {
            if (exception.Code == "cleanup_failed") lease.BlockRecovery();
            else lease.Complete();
            throw;
        }
    }, cancellationToken);

    private void AddGameIndex(List<string> arguments, string? index)
    {
        if (index is null) return;
        arguments.Add("--game-index");
        arguments.Add(paths.Read(index));
    }

    private async Task<CallToolResult> Execute(Func<CancellationToken, Task<CliReport>> operation, CancellationToken cancellationToken)
    {
        try
        {
            CliProcessRunner.EnsureSupportedPlatform(OperatingSystem.IsWindows());
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
            linked.Token.ThrowIfCancellationRequested();
            var result = await operation(linked.Token);
            var report = result.Report;
            var command = report.GetProperty("command").GetString();
            var status = report.TryGetProperty("valid", out var valid) && valid.ValueKind == JsonValueKind.False ? "invalid" : "completed";
            var diagnostics = report.TryGetProperty("diagnostics", out var items) && items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 0;
            var summary = $"{command} {status}. {diagnostics} diagnostics. CLI exit {result.ExitCode}.";
            if (!string.IsNullOrEmpty(result.Stderr)) summary += " CLI stderr is available in _meta.cliStderr.";
            return new CallToolResult
            {
                StructuredContent = report,
                Content = [new TextContentBlock { Text = summary }],
                Meta = new JsonObject { ["cliExitCode"] = result.ExitCode, ["cliStderr"] = result.Stderr }
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

    private static CallToolResult Error(McpFailure failure) => new()
    {
        IsError = true,
        StructuredContent = JsonSerializer.SerializeToElement(new { error = new { code = failure.Code, message = failure.Message, exitCode = failure.ExitCode, stderr = failure.Stderr } }),
        Content = [new TextContentBlock { Text = $"{failure.Code}: {failure.Message}" }]
    };
}
