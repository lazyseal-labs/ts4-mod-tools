using System.Text.Json;
using System.Diagnostics;
using S4MT.Mcp;
using Xunit;

namespace S4MT.Mcp.Tests;

public sealed class PathPolicyTests
{
    [Fact]
    public void DirectAndGameWriteRootsRejectEscapes()
    {
        using var workspace = new TestWorkspace();
        var policy = new PathPolicy(workspace.Options);
        Assert.Equal("path_outside_root", Assert.Throws<McpFailure>(() => policy.Write(Path.Combine(workspace.Root, "..", "outside"))).Code);
        Assert.Equal("read_only_root", Assert.Throws<McpFailure>(() => policy.Write(Path.Combine(workspace.Root, "game", "output"))).Code);
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, "game")));
    }

    [Fact]
    public void ExplicitProjectFilenameAndNestedManifestSourcesAreSupported()
    {
        using var workspace = new TestWorkspace();
        var root = workspace.Project(manifestName: "nested/package.json");
        File.Move(Path.Combine(root, "s4mt.json"), Path.Combine(root, "custom-project.json"));
        File.WriteAllText(Path.Combine(root, "nested", "payload.bin"), "payload");
        File.WriteAllText(Path.Combine(root, "nested", "package.json"), "{\"resources\":[{\"source\":\"payload.bin\"}]}");
        var inputs = new PathPolicy(workspace.Options).ProjectInputs(Path.Combine(root, "custom-project.json"));
        Assert.Contains(Path.Combine(root, "nested", "payload.bin"), inputs);
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("resource")]
    [InlineData("script")]
    [InlineData("snapshot")]
    [InlineData("snapshot-index")]
    public void IndirectLinksCannotEscapeProject(string location)
    {
        using var workspace = new TestWorkspace();
        var project = workspace.Project();
        var outside = Path.Combine(workspace.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "payload"), "{}");
        workspace.DirectoryLink(Path.Combine(project, "linked"), outside);
        switch (location)
        {
            case "manifest":
                File.WriteAllText(Path.Combine(project, "s4mt.json"), "{\"packageManifest\":\"linked/payload\"}");
                break;
            case "resource":
                File.WriteAllText(Path.Combine(project, "package.json"), "{\"resources\":[{\"source\":\"linked/payload\"}]}");
                break;
            case "script":
                File.WriteAllText(Path.Combine(project, "s4mt.json"), "{\"packageManifest\":\"package.json\",\"scriptArchives\":[\"linked/payload\"]}");
                break;
            case "snapshot":
                File.WriteAllText(Path.Combine(project, "s4mt.json"), "{\"packageManifest\":\"package.json\",\"tuningSchema\":{\"snapshot\":\"linked\"}}");
                break;
            case "snapshot-index":
                Directory.CreateDirectory(Path.Combine(project, "schema"));
                workspace.DirectoryLink(Path.Combine(project, "schema", "definitions"), outside);
                File.WriteAllText(Path.Combine(project, "s4mt.json"), "{\"packageManifest\":\"package.json\",\"tuningSchema\":{\"snapshot\":\"schema\"}}");
                break;
        }
        Assert.Throws<S4MT.Cli.CliInputException>(() => new PathPolicy(workspace.Options).ProjectInputs(project));
    }

    [Fact]
    public void DedicatedOutputLockAndRecoveryKeepPublishedFiles()
    {
        using var workspace = new TestWorkspace();
        var root = Path.Combine(workspace.Root, "builds");
        var policy = new PathPolicy(workspace.Options);
        using (var lease = BuildOutputLease.Acquire(root, policy))
        {
            Assert.Equal("output_busy", Assert.Throws<McpFailure>(() => BuildOutputLease.Acquire(root, policy)).Code);
            var stage = Path.Combine(root, $".s4mt-stage-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, "partial"), "partial");
            Directory.CreateDirectory(Path.Combine(root, "published"));
            File.WriteAllText(Path.Combine(root, "published", "keep.package"), "complete");
            var start = new ProcessStartInfo(workspace.Dotnet) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { workspace.Cli, "project", "validate" }) start.ArgumentList.Add(argument);
            using var child = Process.Start(start)!;
            lease.ChildStarted(child);
            child.WaitForExit();
        }
        using var recovered = BuildOutputLease.Acquire(root, policy);
        Assert.Empty(Directory.GetDirectories(root, ".s4mt-stage-*"));
        Assert.Equal("complete", File.ReadAllText(Path.Combine(root, "published", "keep.package")));
    }

    [Fact]
    public void InputsAndUnownedOutputAreNeverDeleted()
    {
        using var workspace = new TestWorkspace();
        var project = workspace.Project();
        var policy = new PathPolicy(workspace.Options);
        Assert.Equal("path_collision", Assert.Throws<McpFailure>(() => policy.NoCollision(workspace.Root, [Path.Combine(project, "s4mt.json")])).Code);
        Assert.Equal("output_not_owned", Assert.Throws<McpFailure>(() => BuildOutputLease.Acquire(project, policy)).Code);
        Assert.True(File.Exists(Path.Combine(project, "s4mt.json")));
    }

    [Fact]
    public void LinkedStagingIsRejectedBeforeTraversalAndDeletion()
    {
        using var workspace = new TestWorkspace();
        var root = Path.Combine(workspace.Root, "builds");
        var policy = new PathPolicy(workspace.Options);
        using (BuildOutputLease.Acquire(root, policy)) { }
        var complete = Path.Combine(root, "complete");
        Directory.CreateDirectory(complete);
        File.WriteAllText(Path.Combine(complete, "keep.package"), "complete");
        workspace.DirectoryLink(Path.Combine(root, $".s4mt-stage-{Guid.NewGuid():N}"), complete);
        Assert.Equal("unsafe_project_path", Assert.Throws<McpFailure>(() => BuildOutputLease.Acquire(root, policy)).Code);
        Assert.Equal("complete", File.ReadAllText(Path.Combine(complete, "keep.package")));
    }

    [Fact]
    public void CopiedMarkerCannotAuthorizeAnotherDirectory()
    {
        using var workspace = new TestWorkspace();
        var first = Path.Combine(workspace.Root, "first");
        var second = Path.Combine(workspace.Root, "second");
        var policy = new PathPolicy(workspace.Options);
        using (BuildOutputLease.Acquire(first, policy)) { }
        Directory.CreateDirectory(second);
        File.Copy(Path.Combine(first, ".s4mt-mcp-owner.json"), Path.Combine(second, ".s4mt-mcp-owner.json"));
        var stage = Path.Combine(second, $".s4mt-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "keep"), "unrelated");
        Assert.Equal("output_not_owned", Assert.Throws<McpFailure>(() => BuildOutputLease.Acquire(second, policy)).Code);
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(stage, "keep")));
    }

    [Fact]
    public void IdleOutputMarkerCannotAuthorizeUnrecordedStaging()
    {
        using var workspace = new TestWorkspace();
        var root = Path.Combine(workspace.Root, "builds");
        var policy = new PathPolicy(workspace.Options);
        using (BuildOutputLease.Acquire(root, policy)) { }
        var stage = Path.Combine(root, $".s4mt-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "keep"), "unrelated");
        Assert.Equal("output_not_owned", Assert.Throws<McpFailure>(() => BuildOutputLease.Acquire(root, policy)).Code);
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(stage, "keep")));
    }

    [Fact]
    public void IncompleteProcessIdentityCannotAuthorizeStagingRecovery()
    {
        using var workspace = new TestWorkspace();
        var root = Path.Combine(workspace.Root, "builds");
        var policy = new PathPolicy(workspace.Options);
        using (BuildOutputLease.Acquire(root, policy)) { }
        var stageName = $".s4mt-stage-{Guid.NewGuid():N}";
        var stage = Path.Combine(root, stageName);
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "keep"), "partial");
        File.WriteAllText(Path.Combine(root, ".s4mt-mcp-owner.json"), JsonSerializer.Serialize(new
        {
            Format = 1,
            OwnerName = "S4MT.Mcp",
            RootPath = root,
            ProcessId = Environment.ProcessId,
            StagingDirectories = new[] { stageName },
            RecoveryBlocked = false
        }));
        var failure = Assert.Throws<McpFailure>(() => BuildOutputLease.Acquire(root, policy));
        Assert.Equal("output_not_owned", failure.Code);
        Assert.Equal("partial", File.ReadAllText(Path.Combine(stage, "keep")));
    }

    [Fact]
    public void MalformedOwnerJsonMapsToOutputNotOwnedAndPreservesStaging()
    {
        using var workspace = new TestWorkspace();
        var root = Path.Combine(workspace.Root, "builds");
        var policy = new PathPolicy(workspace.Options);
        using (BuildOutputLease.Acquire(root, policy)) { }
        var stage = Path.Combine(root, $".s4mt-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "keep"), "partial");
        File.WriteAllText(Path.Combine(root, ".s4mt-mcp-owner.json"), "{");
        var failure = Assert.Throws<McpFailure>(() => BuildOutputLease.Acquire(root, policy));
        Assert.Equal("output_not_owned", failure.Code);
        Assert.Equal("partial", File.ReadAllText(Path.Combine(stage, "keep")));
    }

    [Fact]
    public void BlockRecoveryRetainsKnownProcessAndStagingMetadata()
    {
        using var workspace = new TestWorkspace();
        var root = Path.Combine(workspace.Root, "builds");
        var policy = new PathPolicy(workspace.Options);
        var stageName = $".s4mt-stage-{Guid.NewGuid():N}";
        var stage = Path.Combine(root, stageName);
        using (var lease = BuildOutputLease.Acquire(root, policy))
        using (var child = Process.GetCurrentProcess())
        {
            lease.ChildStarted(child);
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, "keep"), "partial");
            lease.BlockRecovery();
        }
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".s4mt-mcp-owner.json")));
        var owner = marker.RootElement;
        Assert.True(owner.GetProperty("RecoveryBlocked").GetBoolean());
        Assert.Equal(Environment.ProcessId, owner.GetProperty("ProcessId").GetInt32());
        Assert.True(owner.GetProperty("StartTimeTicks").GetInt64() > 0);
        Assert.Contains(stageName, owner.GetProperty("StagingDirectories").EnumerateArray().Select(item => item.GetString()));
        var failure = Assert.Throws<McpFailure>(() => BuildOutputLease.Acquire(root, policy));
        Assert.Equal("cleanup_failed", failure.Code);
        Assert.Equal("partial", File.ReadAllText(Path.Combine(stage, "keep")));
    }

    [Fact]
    public void DanglingLinkCannotBypassConfiguredWriteRoot()
    {
        using var workspace = new TestWorkspace();
        var project = workspace.Project();
        var missingTarget = Path.Combine(workspace.Root, "missing-target");
        workspace.DirectoryLink(Path.Combine(project, "dangling"), missingTarget);
        var policy = new PathPolicy(workspace.Options with { WriteRoots = [project] });
        var error = Assert.Throws<McpFailure>(() => policy.Write(Path.Combine(project, "dangling", "new-file")));
        Assert.Equal(OperatingSystem.IsWindows() ? "path_outside_root" : "unsafe_project_path", error.Code);
        Assert.False(Directory.Exists(missingTarget));
    }
}
