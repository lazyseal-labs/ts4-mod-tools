using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using S4MT.Mcp;
using Xunit;

namespace S4MT.Mcp.Tests;

public sealed class StdioTests
{
    private static Task<McpClient> Connect(TestWorkspace workspace) => McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
    {
        Command = workspace.Dotnet,
        Arguments = [workspace.Host, "--config", workspace.Configuration],
        ShutdownTimeout = TimeSpan.FromSeconds(5)
    }));

    [Fact]
    public async Task OfficialClientDiscoversExactlyThreeTypedToolsAndRejectsMalformedArguments()
    {
        using var workspace = new TestWorkspace();
        await using var client = await Connect(workspace);
        var tools = await client.ListToolsAsync();
        Assert.Equal(new[] { "s4mt_project_build", "s4mt_project_init", "s4mt_project_validate" }, tools.Select(tool => tool.Name).Order());
        var validate = tools.Single(tool => tool.Name == "s4mt_project_validate");
        Assert.Equal("string", validate.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("project").GetProperty("type").GetString());
        var invalid = await client.CallToolAsync("s4mt_project_validate", new Dictionary<string, object?>());
        Assert.True(invalid.IsError);
        Assert.False(File.Exists(Path.Combine(workspace.Root, "child.pid")));
        var valid = await client.CallToolAsync("s4mt_project_validate", new Dictionary<string, object?> { ["project"] = workspace.Project() });
        Assert.NotEqual(true, valid.IsError);
        Assert.True(valid.StructuredContent!.Value.GetProperty("futureReport").GetProperty("retained").GetArrayLength() == 2);
    }

    [Fact]
    public async Task MissingCliReturnsStableErrorThroughProtocol()
    {
        using var workspace = new TestWorkspace();
        workspace.SaveConfig(workspace.Options with { CliPath = Path.Combine(workspace.Root, "missing.dll") });
        await using var client = await Connect(workspace);
        var result = await client.CallToolAsync("s4mt_project_validate", new Dictionary<string, object?> { ["project"] = workspace.Project() });
        Assert.True(result.IsError);
        Assert.Equal("cli_unavailable", result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(3, (await client.ListToolsAsync()).Count);
    }

    [Fact]
    public async Task ExitOneReportAndNestedUnknownFieldsCrossRealStdio()
    {
        using var workspace = new TestWorkspace("invalid");
        await using var client = await Connect(workspace);
        var result = await client.CallToolAsync("s4mt_project_validate", new Dictionary<string, object?> { ["project"] = workspace.Project() });
        Assert.NotEqual(true, result.IsError);
        Assert.False(result.StructuredContent!.Value.GetProperty("valid").GetBoolean());
        Assert.True(result.StructuredContent!.Value.GetProperty("diagnostics")[0].GetProperty("futureDiagnostic").GetProperty("retained").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedCaseAndBomProjectCannotWriteIntoGameRoot(bool bom)
    {
        using var workspace = new TestWorkspace();
        var project = workspace.Project();
        var game = Path.Combine(project, "game");
        workspace.SaveConfig(workspace.Options with { GameRoots = [game] });
        File.WriteAllText(Path.Combine(project, "s4mt.json"), "{\"format\":1,\"name\":\"TestProject\",\"packageManifest\":\"package.json\",\"PackageManifest\":\"game/package.json\",\"scriptArchives\":[]}", new System.Text.UTF8Encoding(bom));
        await using var client = await Connect(workspace);
        var result = await client.CallToolAsync("s4mt_project_init", new Dictionary<string, object?> { ["path"] = project, ["name"] = "TestProject" });
        Assert.True(result.IsError);
        Assert.Equal("read_only_root", result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(game));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "child.pid")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedCaseAndBomSourcesCannotBeDeletedAsStaging(bool bom)
    {
        using var workspace = new TestWorkspace();
        var project = workspace.Project();
        var output = Path.Combine(project, "builds");
        using (BuildOutputLease.Acquire(output, new PathPolicy(workspace.Options))) { }
        var stage = Path.Combine(output, $".s4mt-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        var source = Path.Combine(stage, "input.ts4script");
        File.WriteAllText(source, "input");
        File.WriteAllText(Path.Combine(project, "s4mt.json"), JsonSerializer.Serialize(new
        {
            format = 1, name = "TestProject", PackageManifest = "package.json",
            ScriptArchives = new[] { Path.GetRelativePath(project, source) }
        }), new System.Text.UTF8Encoding(bom));
        await using var client = await Connect(workspace);
        var result = await client.CallToolAsync("s4mt_project_build", new Dictionary<string, object?> { ["project"] = project, ["outputRoot"] = output });
        Assert.True(result.IsError);
        Assert.Equal("path_collision", result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("input", File.ReadAllText(source));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "child.pid")));
    }

    [Fact]
    public async Task CancellationCleansBuildStageAndNextBuildRecovers()
    {
        using var workspace = new TestWorkspace("wait", timeout: 15_000);
        await using var client = await Connect(workspace);
        var output = Path.Combine(workspace.Root, "builds");
        var arguments = new Dictionary<string, object?> { ["project"] = workspace.Project(), ["outputRoot"] = output };
        using var cancellation = new CancellationTokenSource();
        var requestId = new RequestId("cancel-build");
        var call = Task.Run(async () => await client.SendRequestAsync(new JsonRpcRequest
        {
            Id = requestId,
            Method = "tools/call",
            Params = JsonSerializer.SerializeToNode(new { name = "s4mt_project_build", arguments })
        }, cancellation.Token));
        var pid = await workspace.WaitForPid("child.pid");
        var descendant = await workspace.WaitForPid("descendant.pid");
        await client.SendNotificationAsync("notifications/cancelled", new CancelledNotificationParams { RequestId = requestId, Reason = "Cancel the fixture build" });
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        using var deadline = new CancellationTokenSource(5000);
        while (TestWorkspace.Alive(pid) || TestWorkspace.Alive(descendant) || Directory.GetDirectories(output, ".s4mt-stage-*").Length > 0)
            await Task.Delay(20, deadline.Token);
        File.WriteAllText(Path.Combine(workspace.Root, "mode.txt"), "success");
        var next = await client.CallToolAsync("s4mt_project_build", arguments);
        Assert.NotEqual(true, next.IsError);
    }
}
