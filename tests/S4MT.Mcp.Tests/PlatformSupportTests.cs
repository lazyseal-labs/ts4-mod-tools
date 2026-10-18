using System.Text.Json;
using S4MT.Mcp;
using Xunit;

namespace S4MT.Mcp.Tests;

public sealed class PlatformSupportTests
{
    [NonWindowsFact]
    public async Task UnsupportedPlatformRejectsBuildBeforeOutputRecovery()
    {
        using var workspace = new TestWorkspace();
        var project = workspace.Project();
        var output = Path.Combine(workspace.Root, "builds");
        var stageName = $".s4mt-stage-{Guid.NewGuid():N}";
        var stage = Path.Combine(output, stageName);
        Directory.CreateDirectory(stage);
        var sentinel = Path.Combine(stage, "keep");
        File.WriteAllText(sentinel, "manual recovery data");
        var markerPath = Path.Combine(output, ".s4mt-mcp-owner.json");
        var marker = new
        {
            Format = 1,
            OwnerName = "S4MT.Mcp",
            RootPath = output,
            ProcessId = (int?)null,
            StartTimeTicks = (long?)null,
            StagingDirectories = Array.Empty<string>(),
            RecoveryBlocked = true
        };
        File.WriteAllText(markerPath, JsonSerializer.Serialize(marker));

        var tools = new ProjectTools(new PathPolicy(workspace.Options), new CliProcessRunner(workspace.Options), new TestApplicationLifetime());
        var response = await tools.Build(project, output, CancellationToken.None);

        Assert.True(response.IsError);
        Assert.Contains("cli_unavailable", Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(response.Content[0]).Text);
        Assert.True(Directory.Exists(stage));
        Assert.Equal("manual recovery data", File.ReadAllText(sentinel));
        Assert.Equal(JsonSerializer.Serialize(marker), File.ReadAllText(markerPath));
    }
}
