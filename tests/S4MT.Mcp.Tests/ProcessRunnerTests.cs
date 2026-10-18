using System.Diagnostics;
using System.Text.Json;
using S4MT.Mcp;
using Xunit;

namespace S4MT.Mcp.Tests;

public sealed class ProcessRunnerTests
{
    [WindowsFact]
    public async Task ArgumentsRemainSeparateAndUnknownFieldsSurvive()
    {
        using var workspace = new TestWorkspace();
        var trailingSlashes = "path with spaces" + new string('\\', 2);
        var slashesBeforeQuote = "prefix" + new string('\\', 3) + "\"quoted";
        string[] arguments = [
            "project", "validate", "--project", Path.Combine(workspace.Root, "path with spaces & semicolon;"),
            "--literal", "value\"with quote", "--empty", "", "--trailing-slashes", trailingSlashes,
            "--slashes-before-quote", slashesBeforeQuote
        ];
        var result = await new CliProcessRunner(workspace.Options).RunAsync(arguments, "project.validate", CancellationToken.None);
        Assert.Equal(arguments, result.Report.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("unknown", result.Report.GetProperty("futureReport").GetProperty("retained")[0].GetString());
        Assert.True(result.Report.GetProperty("diagnostics")[0].GetProperty("futureDiagnostic").GetProperty("retained").GetBoolean());
    }

    [WindowsFact]
    public async Task ExitOneInvalidReportRemainsStructured()
    {
        using var workspace = new TestWorkspace("invalid");
        var result = await new CliProcessRunner(workspace.Options).RunAsync(["project", "validate"], "project.validate", CancellationToken.None);
        Assert.Equal(1, result.ExitCode);
        Assert.False(result.Report.GetProperty("valid").GetBoolean());
    }

    [WindowsTheory]
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

    [WindowsFact]
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
        if (!OperatingSystem.IsWindows())
        {
            var unsupported = await Assert.ThrowsAsync<McpFailure>(() => new CliProcessRunner(workspace.Options)
                .RunAsync(["project", "validate"], "project.validate", CancellationToken.None));
            Assert.Equal("cli_unavailable", unsupported.Code);
            Assert.False(File.Exists(Path.Combine(workspace.Root, "child.pid")));
            return;
        }
        var run = new CliProcessRunner(workspace.Options).RunAsync(["project", "validate"], "project.validate", CancellationToken.None);
        var descendantPid = await workspace.WaitForPid("descendant.pid");
        var error = await Assert.ThrowsAsync<McpFailure>(() => run);
        Assert.Equal("timeout", error.Code);
        Assert.False(TestWorkspace.Alive(descendantPid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutAndCancellationReapWholeChildTree(bool cancel)
    {
        using var workspace = new TestWorkspace("wait", timeout: cancel ? 10000 : 1500);
        if (!OperatingSystem.IsWindows())
        {
            var unsupported = await Assert.ThrowsAsync<McpFailure>(() => new CliProcessRunner(workspace.Options)
                .RunAsync(["project", "validate"], "project.validate", CancellationToken.None));
            Assert.Equal("cli_unavailable", unsupported.Code);
            Assert.False(File.Exists(Path.Combine(workspace.Root, "child.pid")));
            Assert.False(File.Exists(Path.Combine(workspace.Root, "descendant.pid")));
            return;
        }
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

    [Fact]
    public void UnsupportedPlatformsFailClosedBeforeLaunchingTheConfiguredCli()
    {
        var failure = Assert.Throws<McpFailure>(() => CliProcessRunner.EnsureSupportedPlatform(windows: false));
        Assert.Equal("cli_unavailable", failure.Code);
    }

    [Fact]
    public void ResumeThreadMustReleaseExactlyOneSuspension()
    {
        CliChildProcess.RequireSingleResume(1);
        Assert.Throws<InvalidOperationException>(() => CliChildProcess.RequireSingleResume(0));
        Assert.Throws<InvalidOperationException>(() => CliChildProcess.RequireSingleResume(2));
    }

    [WindowsTheory]
    [InlineData("argument")]
    [InlineData("invalid-operation")]
    public async Task WindowsAdoptionFailureTerminatesSuspendedChildAndMapsCliUnavailable(string failureKind)
    {
        using var workspace = new TestWorkspace();
        var childPid = 0;
        var runner = new CliProcessRunner(workspace.Options, pid =>
        {
            childPid = pid;
            throw failureKind == "argument" ? new ArgumentException("Injected process adoption failure.") :
                new InvalidOperationException("Injected process adoption failure.");
        });
        var failure = await Assert.ThrowsAsync<McpFailure>(() => runner.RunAsync(
            ["project", "validate"], "project.validate", CancellationToken.None));
        Assert.Equal("cli_unavailable", failure.Code);
        Assert.True(childPid > 0);
        Assert.False(TestWorkspace.Alive(childPid));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "child.pid")));
    }

    [WindowsFact]
    public async Task CaptureIOExceptionDoesNotBlockOutputRecoveryAfterTheChildStops()
    {
        using var workspace = new TestWorkspace("success", timeout: 15000);
        var project = workspace.Project();
        var output = Path.Combine(workspace.Root, "output");
        var paths = new PathPolicy(workspace.Options);
        var lifetime = new TestApplicationLifetime();
        var childPid = 0;
        var failingRunner = new CliProcessRunner(workspace.Options, pid =>
        {
            childPid = pid;
            return Process.GetProcessById(pid);
        },
            (_, _, _) => Task.FromException<byte[]>(new IOException("Injected stdout capture failure.")));

        var failedBuild = await new ProjectTools(paths, failingRunner, lifetime).Build(project, output, CancellationToken.None);

        Assert.True(failedBuild.IsError);
        Assert.Equal("cli_io_error", failedBuild.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.True(childPid > 0);
        Assert.False(TestWorkspace.Alive(childPid));
        using (var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, ".s4mt-mcp-owner.json"))))
        {
            Assert.False(marker.RootElement.GetProperty("RecoveryBlocked").GetBoolean());
            Assert.Equal(JsonValueKind.Null, marker.RootElement.GetProperty("ProcessId").ValueKind);
            Assert.Equal(JsonValueKind.Null, marker.RootElement.GetProperty("StartTimeTicks").ValueKind);
        }
        Assert.Empty(Directory.GetDirectories(output, ".s4mt-stage-*"));

        File.WriteAllText(Path.Combine(workspace.Root, "mode.txt"), "success");
        var recoveredBuild = await new ProjectTools(paths, new CliProcessRunner(workspace.Options), lifetime)
            .Build(project, output, CancellationToken.None);
        Assert.NotEqual(true, recoveredBuild.IsError);
    }

    [WindowsFact]
    public async Task ChildStartJournalFailureStopsTheSuspendedJob()
    {
        using var workspace = new TestWorkspace("wait");
        var pid = 0;
        var runner = new CliProcessRunner(workspace.Options);

        var failure = await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(
            ["project", "validate"], "project.validate", CancellationToken.None,
            child =>
            {
                pid = child.Id;
                throw new IOException("Injected owner-marker write failure.");
            }));

        Assert.Contains("owner-marker", failure.Message);
        Assert.True(pid > 0);
        Assert.False(TestWorkspace.Alive(pid));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "child.pid")));
    }
}
