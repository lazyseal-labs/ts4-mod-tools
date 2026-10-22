using System.Text;
using System.Text.Json;
using S4MT.Cli;
using S4MT.Mcp;
using Xunit;

namespace S4MT.Mcp.Tests;

public sealed class CommandSafetyTests
{
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
