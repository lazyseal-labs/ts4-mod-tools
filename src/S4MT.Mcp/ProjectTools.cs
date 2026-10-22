using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;

namespace S4MT.Mcp;

internal sealed class ProjectTools(
    PathPolicy paths,
    CliProcessRunner runner,
    IHostApplicationLifetime lifetime,
    GameIndexStore? gameIndexStore = null)
{
    public Task<CallToolResult> Init(
        string path,
        string name,
        CancellationToken cancellationToken) => Execute(async token =>
    {
        var project = paths.Output(path);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || !char.IsAsciiLetterOrDigit(name[0]) ||
            name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
            throw new McpFailure("invalid_arguments", "Use a package-safe project name with 1 to 64 characters. Directory paths may contain spaces.");
        paths.ProjectInputs(project, initialize: true);
        return await runner.RunAsync(["project", "init", "--path", project, "--name", name], "project.init", token);
    }, cancellationToken);

    public Task<CallToolResult> Validate(
        string project,
        CancellationToken cancellationToken,
        string? gameIndex = null)
    {
        GameIndexArtifact? usedCache = null;
        return Execute(async token =>
        {
            var input = paths.Read(project);
            paths.ProjectInputs(input);
            var arguments = new List<string> { "project", "validate", "--project", input };
            AddGameIndex(arguments, SelectGameIndex(gameIndex, out usedCache));
            return await runner.RunAsync(arguments, "project.validate", token);
        }, cancellationToken, () => usedCache);
    }

    public Task<CallToolResult> Build(
        string project,
        string outputRoot,
        CancellationToken cancellationToken,
        string? gameIndex = null)
    {
        GameIndexArtifact? usedCache = null;
        return Execute(async token =>
        {
            var input = paths.Read(project);
            var output = paths.Output(outputRoot);
            var inputs = paths.ProjectInputs(input);
            var arguments = new List<string> { "project", "build", "--project", input, "--output-root", output };
            var selectedGameIndex = SelectGameIndex(gameIndex, out usedCache);
            AddGameIndex(arguments, selectedGameIndex);
            paths.NoCollision(output, selectedGameIndex is null ? inputs : inputs.Append(selectedGameIndex));
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
        }, cancellationToken, () => usedCache);
    }

    private string? SelectGameIndex(string? requestedIndex, out GameIndexArtifact? selectedArtifact)
    {
        selectedArtifact = null;
        if (requestedIndex is not null) return paths.Read(requestedIndex);
        selectedArtifact = gameIndexStore?.Active;
        return selectedArtifact is null ? null : paths.GameIndex(selectedArtifact.Path);
    }

    private static void AddGameIndex(List<string> arguments, string? index)
    {
        if (index is null) return;
        arguments.Add("--game-index");
        arguments.Add(index);
    }

    private Task<CallToolResult> Execute(
        Func<CancellationToken, Task<CliReport>> operation,
        CancellationToken cancellationToken,
        Func<GameIndexArtifact?>? gameIndexProvenance = null) =>
        ToolResult.Execute(lifetime, operation, cancellationToken, gameIndexProvenance);
}
