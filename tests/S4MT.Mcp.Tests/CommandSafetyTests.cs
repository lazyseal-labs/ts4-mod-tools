using System.Text;
using System.Text.Json;
using S4MT.Cli;
using S4MT.Mcp;
using Xunit;

namespace S4MT.Mcp.Tests;

public sealed class CommandSafetyTests
{
    [Fact]
    public void StandaloneOutputsCannotEnterManagedProjectBuildTrees()
    {
        using var workspace = new TestWorkspace();
        var policy = new PathPolicy(workspace.Options);
        var root = Path.Combine(workspace.Root, "builds");
        using (BuildOutputLease.Acquire(root, policy))
        {
            Assert.Equal("output_in_project_build", Assert.Throws<McpFailure>(() => policy.StandaloneOutput(root)).Code);
            var output = Path.Combine(root, "published", "other.package");
            Assert.Equal("output_in_project_build", Assert.Throws<McpFailure>(() => CommandOutputLease.Acquire(output, false, policy, [])).Code);
            Assert.False(Directory.Exists(Path.GetDirectoryName(output)));
        }
        Assert.Equal("output_in_project_build", Assert.Throws<McpFailure>(() => policy.StandaloneOutput(Path.Combine(root, "other.package"))).Code);
    }

    [Theory]
    [InlineData(".s4mt-mcp-owner.json")]
    [InlineData(".s4mt-mcp.lock")]
    [InlineData(".s4mt-mcp-lease.lock")]
    [InlineData(".s4mt-mcp-stage-owned/payload.bin")]
    [InlineData(".s4mt-stage-owned/payload.bin")]
    public void CallerOutputsCannotTargetInternalState(string relative)
    {
        using var workspace = new TestWorkspace();
        var policy = new PathPolicy(workspace.Options);
        Assert.Equal("reserved_output_path", Assert.Throws<McpFailure>(() => policy.Output(Path.Combine(workspace.Root, relative))).Code);
    }

    [Fact]
    public void MissingTreeOutputUnderLinkedParentCannotEscapeWriteRoot()
    {
        using var workspace = new TestWorkspace();
        using var outside = new TestWorkspace();
        var write = Path.Combine(workspace.Root, "write");
        Directory.CreateDirectory(write);
        var link = Path.Combine(write, "linked");
        workspace.DirectoryLink(link, outside.Root);
        var policy = new PathPolicy(workspace.Options with { WriteRoots = [write] });
        var output = Path.Combine(link, "new-bundle");
        Assert.Equal("path_outside_root", Assert.Throws<McpFailure>(() => policy.CheckTree(output, write: true)).Code);
        Assert.Equal("path_outside_root", Assert.Throws<McpFailure>(() => CommandOutputLease.Acquire(output, true, policy, [])).Code);
        Assert.False(Directory.Exists(Path.Combine(outside.Root, "new-bundle")));
    }

    [Fact]
    public void WriteOnlyStagingIsCleanedWithoutGrantingInputReadAccess()
    {
        using var workspace = new TestWorkspace();
        var read = Path.Combine(workspace.Root, "read");
        var write = Path.Combine(workspace.Root, "write");
        Directory.CreateDirectory(read);
        Directory.CreateDirectory(write);
        var policy = new PathPolicy(workspace.Options with { ReadRoots = [read], WriteRoots = [write] });
        var output = Path.Combine(write, "result.bin");
        using (var lease = CommandOutputLease.Acquire(output, false, policy, []))
        {
            File.WriteAllText(lease.StagedFile!, "result");
            lease.Publish();
        }
        Assert.Equal("result", File.ReadAllText(output));
        Assert.Empty(Directory.GetDirectories(write, ".s4mt-mcp-stage-*"));
        Assert.Equal("path_outside_root", Assert.Throws<McpFailure>(() => policy.Read(output)).Code);
    }

    [Fact]
    public void TrailingSeparatorUsesSameBundleLockWithoutCreatingDestination()
    {
        using var workspace = new TestWorkspace();
        var policy = new PathPolicy(workspace.Options);
        var output = Path.Combine(workspace.Root, "bundle");
        using var lease = CommandOutputLease.Acquire(output + Path.DirectorySeparatorChar, true, policy, []);
        Assert.False(Directory.Exists(output));
        Assert.Equal("output_busy", Assert.Throws<McpFailure>(() => CommandOutputLease.Acquire(output, true, policy, [])).Code);
        var root = Path.GetPathRoot(workspace.Root)!;
        Assert.Equal(root, HostOptions.Absolute(root));
    }

    [Fact]
    public void CatalogCoversEveryCliReportAndRejectsUnlistedOptions()
    {
        var source = File.ReadAllText(Path.Combine(TestWorkspace.RepositoryRoot, "src", "S4MT.Cli", "S4mtCli.cs"));
        var reports = System.Text.RegularExpressions.Regex.Matches(source, "(?:command = |WriteProjectValidation\\()\"([a-z.]+)\"")
            .Select(match => match.Groups[1].Value).Where(command => command != "version").Distinct().Order();
        Assert.Equal(reports, CommandCatalog.All.Select(command => command.Command).Order());
        foreach (var descriptor in CommandCatalog.All)
        {
            Assert.NotEmpty(descriptor.Fields);
            CommandCatalog.ValidateInvocation(descriptor.Words.Concat(descriptor.Options.SelectMany(option => new[] { option.Flag, "value" })).ToArray());
            Assert.Throws<CliInputException>(() => CommandCatalog.ValidateInvocation([.. descriptor.Words, "--unlisted", "value"]));
            var schema = new CommandTool(descriptor).ProtocolTool.InputSchema;
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal(descriptor.Options.Length, schema.GetProperty("properties").EnumerateObject().Count());
        }
    }

    [Theory]
    [InlineData("inspect", "{}")]
    [InlineData("inspect", "{\"package\":7}")]
    [InlineData("inspect", "{\"package\":\"p\",\"args\":\"--help\"}")]
    [InlineData("game.find", "{\"index\":\"p\",\"type\":\"1\",\"limit\":\"1\"}")]
    [InlineData("game.find", "{\"index\":\"p\",\"type\":\"1\",\"limit\":1.5}")]
    [InlineData("image.export", "{\"package\":\"p\",\"tgi\":\"1\"}")]
    [InlineData("image.export", "{\"package\":\"p\",\"tgi\":\"1\",\"output\":\"x\",\"bundle\":\"x\"}")]
    [InlineData("image.build", "{\"tgi\":\"1\",\"output\":\"x\",\"png\":\"x\",\"bundle\":\"x\"}")]
    [InlineData("image.import", "{\"package\":\"p\",\"tgi\":\"1\",\"output\":\"x\"}")]
    [InlineData("tuning.validate", "{\"xml\":\"x\",\"snapshot\":\"s\"}")]
    [InlineData("simdata.set", "{\"package\":\"p\",\"tgi\":\"1\",\"output\":\"x\",\"table\":\"t\",\"row\":\"0\",\"column\":\"c\",\"value\":\"7\"}")]
    public void MalformedTypedArgumentsFailBeforeCli(string command, string json)
    {
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        Assert.Equal("invalid_arguments", Assert.Throws<McpFailure>(() => CommandTool.Parse(CommandCatalog.Get(command), arguments)).Code);
    }

    [Theory]
    [InlineData("Manifest", false)]
    [InlineData("Manifest", true)]
    [InlineData("MergeManifest", false)]
    [InlineData("MergeManifest", true)]
    public void StandaloneIndirectInputsRespectRootsAndBom(string roleName, bool bom)
    {
        var role = Enum.Parse<OptionRole>(roleName);
        using var workspace = new TestWorkspace();
        var input = Path.Combine(workspace.Root, "input.bin");
        File.WriteAllText(input, "data");
        var manifest = Path.Combine(workspace.Root, "manifest.json");
        var document = role == OptionRole.Manifest
            ? new { ReSoUrCeS = new[] { new { SoUrCe = input } } } as object
            : new { PaCkAgEs = new[] { input } };
        File.WriteAllText(manifest, JsonSerializer.Serialize(document), new UTF8Encoding(bom));
        var policy = new PathPolicy(workspace.Options);
        Assert.Contains(input, policy.CommandInputs(manifest, role));
        var outside = Path.Combine(workspace.Root, "..", "outside.bin");
        File.WriteAllText(manifest, role == OptionRole.Manifest
            ? JsonSerializer.Serialize(new { Resources = new[] { new { Source = outside } } })
            : JsonSerializer.Serialize(new { Packages = new[] { outside } }), new UTF8Encoding(bom));
        Assert.Equal("path_outside_root", Assert.Throws<McpFailure>(() => policy.CommandInputs(manifest, role)).Code);
    }

    [Theory]
    [InlineData("Tree")]
    [InlineData("Bundle")]
    public void TreeReadsRejectLinkedChildrenOutsideTheInput(string roleName)
    {
        var role = Enum.Parse<OptionRole>(roleName);
        using var workspace = new TestWorkspace();
        var tree = Path.Combine(workspace.Root, "tree");
        var outside = Path.Combine(workspace.Root, "outside");
        Directory.CreateDirectory(tree);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "sentinel"), "keep");
        workspace.DirectoryLink(Path.Combine(tree, "link"), outside);
        Assert.Throws<CliInputException>(() => new PathPolicy(workspace.Options).CommandInputs(tree, role));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "sentinel")));
    }

    [Fact]
    public void OutputReservationSerializesWritersAndPreservesUnownedFiles()
    {
        using var workspace = new TestWorkspace();
        var policy = new PathPolicy(workspace.Options);
        var output = Path.Combine(workspace.Root, "output.bin");
        var sentinel = Path.Combine(workspace.Root, ".s4mt-mcp-stage-unowned");
        Directory.CreateDirectory(sentinel);
        File.WriteAllText(Path.Combine(sentinel, "keep.txt"), "keep");
        File.WriteAllText(output, "previous");
        using (var first = CommandOutputLease.Acquire(output, false, policy, []))
        {
            Assert.Equal("output_busy", Assert.Throws<McpFailure>(() => CommandOutputLease.Acquire(output, false, policy, [])).Code);
            File.WriteAllText(first.StagedFile!, "next");
            Assert.Equal("previous", File.ReadAllText(output));
            first.Publish();
            Assert.Equal("next", File.ReadAllText(output));
        }
        var lockPath = Directory.GetFiles(workspace.Root, ".s4mt-mcp-*.lock").Single();
        using (CommandOutputLease.Acquire(output, false, policy, [])) { }
        Assert.True(File.Exists(lockPath));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(sentinel, "keep.txt")));
        Assert.Equal("next", File.ReadAllText(output));
    }

    [Fact]
    public void CliAndDirectoryInputsCannotBecomeOutputs()
    {
        using var workspace = new TestWorkspace();
        var policy = new PathPolicy(workspace.Options);
        Assert.Equal("path_collision", Assert.Throws<McpFailure>(() => policy.NoCollision(workspace.Cli, [])).Code);
        var tree = Path.Combine(workspace.Root, "source");
        Directory.CreateDirectory(tree);
        Assert.Equal("path_collision", Assert.Throws<McpFailure>(() => policy.NoCollision(Path.Combine(tree, "output"), [tree])).Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(tree));
        var reference = File.ReadAllText(Path.Combine(TestWorkspace.RepositoryRoot, "docs", "mcp-tools.md"));
        foreach (var descriptor in CommandCatalog.All) Assert.Contains($"| `{descriptor.ToolName}` |", reference);
    }

    [Fact]
    public void UnownedLockAndUnconfirmedChildCleanupBlockRecovery()
    {
        using var workspace = new TestWorkspace();
        var policy = new PathPolicy(workspace.Options);
        var output = Path.Combine(workspace.Root, "output.bin");
        using (CommandOutputLease.Acquire(output, false, policy, [])) { }
        var lockPath = Directory.GetFiles(workspace.Root, ".s4mt-mcp-*.lock").Single();
        File.WriteAllText(lockPath, "unowned sentinel");
        Assert.Equal("output_not_owned", Assert.Throws<McpFailure>(() => CommandOutputLease.Acquire(output, false, policy, [])).Code);
        Assert.Equal("unowned sentinel", File.ReadAllText(lockPath));
        File.WriteAllText(lockPath, "S4MT.Mcp output lease v1\n");
        string stage;
        using (var lease = CommandOutputLease.Acquire(output, false, policy, []))
        {
            stage = lease.StagedFile!;
            File.WriteAllText(stage, "preserve for inspection");
            lease.BlockRecovery();
        }
        Assert.Equal("cleanup_failed", Assert.Throws<McpFailure>(() => CommandOutputLease.Acquire(output, false, policy, [])).Code);
        Assert.Equal("preserve for inspection", File.ReadAllText(stage));
    }
}
