using System.Text.Json;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;
using S4MT.Cli;

namespace S4MT.Mcp;

internal sealed class CommandService(PathPolicy paths, CliProcessRunner runner, IHostApplicationLifetime lifetime, ProjectTools projects)
{
    internal Task<CallToolResult> RunAsync(CommandDescriptor descriptor, IDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken)
    {
        Dictionary<string, string> values;
        try { values = CommandTool.Parse(descriptor, arguments); }
        catch (McpFailure failure) { return Task.FromResult(ToolResult.Error(failure)); }
        values.TryGetValue("--game-index", out var gameIndex);
        return descriptor.Command switch
        {
            "project.init" => projects.Init(values["--path"], values["--name"], cancellationToken),
            "project.validate" => projects.Validate(values["--project"], cancellationToken, gameIndex),
            "project.build" => projects.Build(values["--project"], values["--output-root"], cancellationToken, gameIndex),
            _ => RunCommandAsync(descriptor, values, cancellationToken)
        };
    }

    private Task<CallToolResult> RunCommandAsync(CommandDescriptor descriptor, Dictionary<string, string> values, CancellationToken cancellationToken) =>
        ToolResult.Execute(lifetime, async token =>
        {
            var inputs = new List<string>();
            var outputs = new List<(string Flag, string Path, bool Tree)>();
            foreach (var option in descriptor.Options)
            {
                if (!values.TryGetValue(option.Flag, out var value)) continue;
                if (option.Role is OptionRole.Output or OptionRole.OutputTree)
                {
                    value = paths.StandaloneOutput(value);
                    outputs.Add((option.Flag, value, option.Role == OptionRole.OutputTree));
                }
                else if (option.Role is not (OptionRole.Text or OptionRole.Int))
                {
                    value = paths.Read(value);
                    inputs.AddRange(paths.CommandInputs(value, option.Role));
                }
                values[option.Flag] = value;
            }
            foreach (var output in outputs) paths.NoCollision(output.Path, inputs);
            if (outputs.Count == 0) return await Invoke(values, token);
            var destination = outputs.Single();
            using var lease = CommandOutputLease.Acquire(destination.Path, destination.Tree, paths, inputs);
            if (!destination.Tree)
            {
                values[destination.Flag] = lease.StagedFile!;
                if (descriptor.Command == "game.index" && File.Exists(destination.Path))
                    File.Copy(destination.Path, lease.StagedFile!);
            }
            try
            {
                var report = await Invoke(values, token);
                lease.Publish();
                return lease.Rewrite(report);
            }
            catch (McpFailure failure) when (failure.Code == "cleanup_failed")
            {
                lease.BlockRecovery();
                throw;
            }

            Task<CliReport> Invoke(Dictionary<string, string> options, CancellationToken invocationToken)
            {
                var cliArguments = new List<string>(descriptor.Words);
                foreach (var option in descriptor.Options)
                    if (options.TryGetValue(option.Flag, out var value))
                    {
                        cliArguments.Add(option.Flag);
                        cliArguments.Add(value);
                    }
                return runner.RunAsync(cliArguments, descriptor.Command, invocationToken);
            }
        }, cancellationToken);
}
