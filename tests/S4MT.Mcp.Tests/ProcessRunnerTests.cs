using S4MT.Mcp;
using Xunit;

namespace S4MT.Mcp.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task ArgumentsRemainSeparateAndUnknownFieldsSurvive()
    {
        using var workspace = new TestWorkspace();
        string[] arguments = ["project", "validate", "--project", Path.Combine(workspace.Root, "path with spaces & semicolon;"), "--literal", "value\"with quote"];
        var result = await new CliProcessRunner(workspace.Options).RunAsync(arguments, "project.validate", CancellationToken.None);
        Assert.Equal(arguments, result.Report.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("unknown", result.Report.GetProperty("futureReport").GetProperty("retained")[0].GetString());
        Assert.True(result.Report.GetProperty("diagnostics")[0].GetProperty("futureDiagnostic").GetProperty("retained").GetBoolean());
    }

    [Fact]
    public async Task ExitOneInvalidReportRemainsStructured()
    {
        using var workspace = new TestWorkspace("invalid");
        var result = await new CliProcessRunner(workspace.Options).RunAsync(["project", "validate"], "project.validate", CancellationToken.None);
        Assert.Equal(1, result.ExitCode);
        Assert.False(result.Report.GetProperty("valid").GetBoolean());
    }

    [Theory]
    [InlineData("malformed", "malformed_cli_output")]
    [InlineData("array", "malformed_cli_output")]
    [InlineData("error", "cli_error")]
    [InlineData("flood", "output_limit")]
    [InlineData("stderr-flood", "output_limit")]
    public async Task StableErrors(string mode, string code)
    {
        using var workspace = new TestWorkspace(mode, outputLimit: 4096);
        var error = await Assert.ThrowsAsync<McpFailure>(() => new CliProcessRunner(workspace.Options).RunAsync(["project", "validate"], "project.validate", CancellationToken.None));
        Assert.Equal(code, error.Code);
        if (mode == "error") Assert.Contains("invalid_input", error.Stderr);
    }

    [Fact]
    public async Task MissingEntrypointIsActionable()
    {
        using var workspace = new TestWorkspace();
        var error = await Assert.ThrowsAsync<McpFailure>(() => new CliProcessRunner(workspace.Options with { CliPath = Path.Combine(workspace.Root, "missing.dll") }).RunAsync([], "project.validate", CancellationToken.None));
        Assert.Equal("cli_unavailable", error.Code);
        Assert.Contains("missing.dll", error.Message);
    }

    [Fact]
    public async Task DescendantStopsEvenWhenParentExitsFirst()
    {
        using var workspace = new TestWorkspace("orphan", timeout: 1500);
        var run = new CliProcessRunner(workspace.Options).RunAsync(["project", "validate"], "project.validate", CancellationToken.None);
        var descendantPid = await workspace.WaitForPid("descendant.pid");
        var error = await Assert.ThrowsAsync<McpFailure>(() => run);
        Assert.Equal(OperatingSystem.IsWindows() ? "timeout" : "cleanup_failed", error.Code);
        if (OperatingSystem.IsWindows()) Assert.False(TestWorkspace.Alive(descendantPid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutAndCancellationReapWholeChildTree(bool cancel)
    {
        using var workspace = new TestWorkspace("wait", timeout: cancel ? 10000 : 1500);
        using var cancellation = new CancellationTokenSource();
        var run = new CliProcessRunner(workspace.Options).RunAsync(["project", "validate"], "project.validate", cancellation.Token);
        var parentPid = await workspace.WaitForPid("child.pid");
        var descendantPid = await workspace.WaitForPid("descendant.pid");
        if (cancel) cancellation.Cancel();
        var error = await Assert.ThrowsAsync<McpFailure>(() => run);
        Assert.Equal(cancel ? "cancelled" : "timeout", error.Code);
        Assert.False(TestWorkspace.Alive(parentPid));
        Assert.False(TestWorkspace.Alive(descendantPid));
        File.WriteAllText(Path.Combine(workspace.Root, "mode.txt"), "success");
        Assert.Equal(0, (await new CliProcessRunner(workspace.Options).RunAsync(["project", "validate"], "project.validate", CancellationToken.None)).ExitCode);
    }
}
