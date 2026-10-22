using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using S4MT.Cli;

namespace S4MT.Mcp;

internal sealed class CommandTool(CommandDescriptor descriptor) : McpServerTool
{
    public override IReadOnlyList<object> Metadata => [];

    public override Tool ProtocolTool { get; } = new()
    {
        Name = descriptor.ToolName,
        Description = $"Run S4MT {descriptor.Command}. Returns the original CLI JSON report. Paths must be absolute and inside configured roots. Game roots and active Mods are read-only.",
        InputSchema = Schema(descriptor),
        Annotations = new ToolAnnotations { ReadOnlyHint = descriptor.ReadOnly, IdempotentHint = descriptor.ReadOnly || descriptor.Command == "project.build", DestructiveHint = false, OpenWorldHint = false }
    };

    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default) =>
        new(request.Services!.GetRequiredService<CommandService>().RunAsync(descriptor, request.Params?.Arguments, cancellationToken));

    private static JsonElement Schema(CommandDescriptor command)
    {
        var properties = new JsonObject();
        foreach (var option in command.Options)
            properties[option.Name] = new JsonObject
            {
                ["type"] = option.Role == OptionRole.Int ? "integer" : "string",
                ["description"] = option.Role switch
                {
                    OptionRole.Output or OptionRole.OutputTree => $"Absolute writable {option.Name} path. File outputs replace existing files after success; bundle exports require a new directory.",
                    OptionRole.Project => "Absolute project directory or project JSON file under a configured read root.",
                    OptionRole.Read or OptionRole.Tree or OptionRole.Bundle or OptionRole.Manifest or OptionRole.MergeManifest => $"Absolute {option.Name} input path under a configured read or game root.",
                    _ => $"CLI {option.Flag} value."
                }
            };
        return JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object", ["properties"] = properties,
            ["required"] = new JsonArray(command.Options.Where(option => option.Required).Select(option => (JsonNode?)JsonValue.Create(option.Name)).ToArray()),
            ["additionalProperties"] = false
        });
    }

    internal static Dictionary<string, string> Parse(CommandDescriptor descriptor, IDictionary<string, JsonElement>? arguments)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in arguments ?? new Dictionary<string, JsonElement>())
        {
            var option = descriptor.Options.SingleOrDefault(option => option.Name == pair.Key)
                ?? throw new McpFailure("invalid_arguments", $"Unknown parameter '{pair.Key}'.");
            if (option.Role == OptionRole.Int)
            {
                if (pair.Value.ValueKind != JsonValueKind.Number || !pair.Value.TryGetInt32(out var integer)) throw new McpFailure("invalid_arguments", $"{option.Name} must be a 32-bit integer.");
                result[option.Flag] = integer.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                if (pair.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(pair.Value.GetString()))
                    throw new McpFailure("invalid_arguments", $"{option.Name} must be a non-empty string.");
                result[option.Flag] = pair.Value.GetString()!;
            }
        }
        foreach (var option in descriptor.Options.Where(option => option.Required))
            if (!result.ContainsKey(option.Flag)) throw new McpFailure("invalid_arguments", $"Required parameter '{option.Name}' was not provided.");
        if (descriptor.Command.StartsWith("image.", StringComparison.Ordinal))
        {
            var alternative = descriptor.Command == "image.export" ? "--output" : "--png";
            if (result.ContainsKey(alternative) == result.ContainsKey("--bundle"))
                throw new McpFailure("invalid_arguments", $"Provide exactly one of {alternative} and --bundle.");
        }
        if (descriptor.Command is "tuning.build" or "tuning.import" or "tuning.validate")
        {
            var count = new[] { "--snapshot", "--game-version", "--snapshot-sha256" }.Count(result.ContainsKey);
            if (count is not (0 or 3)) throw new McpFailure("invalid_arguments", "Provide snapshot, gameVersion and snapshotSha256 together.");
        }
        return result;
    }
}
