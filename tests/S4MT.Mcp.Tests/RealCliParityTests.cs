using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using S4MT.Cli;
using S4MT.Mcp;
using Xunit;

namespace S4MT.Mcp.Tests;

public sealed class RealCliParityTests
{
    [Fact]
    public async Task ResourceExportCannotResetBlockedOutputReservation()
    {
        using var workspace = new TestWorkspace(timeout: 20000);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cli = Path.Combine(TestWorkspace.RepositoryRoot, "src", "S4MT.Cli", "bin", configuration, "net10.0", "s4mt.dll");
        workspace.SaveConfig(workspace.Options with { CliPath = cli });
        var policy = new PathPolicy(workspace.Options);
        var blockedOutput = Path.Combine(workspace.Root, "blocked.bin");
        using (var lease = CommandOutputLease.Acquire(blockedOutput, false, policy, [])) lease.BlockRecovery();
        var lockPath = Directory.GetFiles(workspace.Root, ".s4mt-mcp-*.lock").Single();
        var blockedState = File.ReadAllText(lockPath);
        var payload = Path.Combine(workspace.Root, "ready.bin");
        File.WriteAllText(payload, "S4MT.Mcp output lease v1\n");
        var manifest = Path.Combine(workspace.Root, "manifest.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new { format = 1, resources = new[] { new { type = "0xE882D22F", group = "0", instance = "1", source = payload, encoding = "bytes" } } }));
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = workspace.Dotnet, Arguments = [workspace.Host, "--config", workspace.Configuration]
        }));
        var package = Path.Combine(workspace.Root, "source.package");
        var build = await client.CallToolAsync("s4mt_build", new Dictionary<string, object?> { ["manifest"] = manifest, ["output"] = package });
        Assert.False(build.IsError == true, JsonSerializer.Serialize(build));
        var export = await client.CallToolAsync("s4mt_resource_export", new Dictionary<string, object?>
        {
            ["package"] = package, ["tgi"] = "0xE882D22F:0x00000000:0x0000000000000001", ["output"] = lockPath
        });
        Assert.True(export.IsError);
        Assert.Equal("reserved_output_path", export.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(blockedState, File.ReadAllText(lockPath));
        Assert.Equal("cleanup_failed", Assert.Throws<McpFailure>(() => CommandOutputLease.Acquire(blockedOutput, false, policy, [])).Code);
    }

    [Fact]
    public async Task DisjointRootsSupportFilePublicationAndIndexReuse()
    {
        using var workspace = new TestWorkspace(timeout: 20000, outputLimit: 1048576);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cli = Path.Combine(TestWorkspace.RepositoryRoot, "src", "S4MT.Cli", "bin", configuration, "net10.0", "s4mt.dll");
        var read = Path.Combine(workspace.Root, "read");
        var write = Path.Combine(workspace.Root, "write");
        var game = Path.Combine(read, "game");
        Directory.CreateDirectory(read);
        Directory.CreateDirectory(write);
        Directory.CreateDirectory(Path.Combine(game, "Data", "Client"));
        var json = Path.Combine(read, "strings.json");
        File.WriteAllText(json, JsonSerializer.Serialize(new { format = 1, tgi = "0x220557DA:0x00000000:0x0000000000000001", entries = new[] { new { key = "0x12345678", text = "Hello" } } }));
        workspace.SaveConfig(workspace.Options with { CliPath = cli, ReadRoots = [read], WriteRoots = [write], GameRoots = [game] });
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = workspace.Dotnet, Arguments = [workspace.Host, "--config", workspace.Configuration]
        }));
        var package = Path.Combine(write, "strings.package");
        var built = await client.CallToolAsync("s4mt_stbl_build", new Dictionary<string, object?> { ["json"] = json, ["output"] = package });
        Assert.False(built.IsError == true, JsonSerializer.Serialize(built));
        Assert.True(File.Exists(package));
        File.Copy(package, Path.Combine(game, "Data", "Client", "ClientFullBuild0.package"));
        var args = new Dictionary<string, object?> { ["gameRoot"] = game, ["output"] = Path.Combine(write, "index.jsonl.gz") };
        var indexed = await client.CallToolAsync("s4mt_game_index", args);
        Assert.False(indexed.IsError == true, JsonSerializer.Serialize(indexed));
        var reused = await client.CallToolAsync("s4mt_game_index", args);
        Assert.False(reused.IsError == true, JsonSerializer.Serialize(reused));
        Assert.True(reused.StructuredContent!.Value.GetProperty("reused").GetBoolean());
        Assert.Empty(Directory.GetDirectories(write, ".s4mt-mcp-stage-*"));
        var denied = await client.CallToolAsync("s4mt_inspect", new Dictionary<string, object?> { ["package"] = package });
        Assert.True(denied.IsError);
        Assert.Equal("path_outside_root", denied.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task All32ToolsRunRealCliWithGeneratedFixturesAndReadback()
    {
        using var workspace = new TestWorkspace(timeout: 20000, outputLimit: 1048576);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cli = Path.Combine(TestWorkspace.RepositoryRoot, "src", "S4MT.Cli", "bin", configuration, "net10.0", "s4mt.dll");
        workspace.SaveConfig(workspace.Options with { CliPath = cli });
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = workspace.Dotnet, Arguments = [workspace.Host, "--config", workspace.Configuration]
        }));
        var tools = await client.ListToolsAsync();
        Assert.Equal(CommandCatalog.All.Select(command => command.ToolName).Order(), tools.Select(tool => tool.Name).Order());
        var invoked = new HashSet<string>();
        string P(string name) => Path.Combine(workspace.Root, name);
        void Json(string name, object value) => File.WriteAllText(P(name), JsonSerializer.Serialize(value));
        async Task<JsonElement> Call(string command, object arguments)
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments))!;
            var result = await client.CallToolAsync("s4mt_" + command.Replace('.', '_'), values);
            Assert.False(result.IsError == true, $"{command}: {JsonSerializer.Serialize(result)}");
            var report = result.StructuredContent!.Value;
            Assert.Equal(command, report.GetProperty("command").GetString());
            invoked.Add(command);
            return report;
        }

        File.WriteAllText(P("payload.txt"), "hello package");
        File.WriteAllBytes(P("casp.bin"), RealCliFixtures.Casp());
        File.WriteAllBytes(P("simdata.bin"), RealCliFixtures.SimData());
        File.WriteAllBytes(P("combined.bin"), RealCliFixtures.CombinedTuning());
        var tgi = "0xE882D22F:0x00000000:0x0000000000000001";
        var caspTgi = "0x034AEECB:0x00000000:0x0000000000000002";
        var simdataTgi = "0x545AC67A:0x00000000:0x0000000000000003";
        var combinedTgi = "0x62E94D38:0x00000000:0x0000000000000004";
        Json("manifest.json", new { format = 1, resources = new[]
        {
            new { type = "0xE882D22F", group = "0", instance = "1", source = P("payload.txt"), encoding = "utf8" },
            new { type = "0x034AEECB", group = "0", instance = "2", source = "casp.bin", encoding = "bytes" },
            new { type = "0x545AC67A", group = "0", instance = "3", source = "simdata.bin", encoding = "bytes" },
            new { type = "0x62E94D38", group = "0", instance = "4", source = "combined.bin", encoding = "bytes" }
        }});
        var built = await Call("build", new { manifest = P("manifest.json"), output = P("source.package") });
        Assert.True(built.GetProperty("verified").GetBoolean());
        Assert.Equal(P("source.package"), built.GetProperty("output").GetString());
        Assert.Equal(4, (await Call("inspect", new { package = P("source.package") })).GetProperty("resourceCount").GetInt32());
        Assert.True((await Call("verify", new { manifest = P("manifest.json"), package = P("source.package") })).GetProperty("valid").GetBoolean());
        await Call("resource.export", new { package = P("source.package"), tgi, output = P("export.txt") });
        Assert.Equal("hello package", File.ReadAllText(P("export.txt")));
        await Call("resource.import", new { package = P("source.package"), tgi, payload = P("export.txt"), output = P("import.package") });
        Json("merge.json", new { format = 1, packages = new[] { "source.package", P("import.package") }, onConflict = "first" });
        Assert.True((await Call("package.merge", new { manifest = P("merge.json"), output = P("merged.package") })).GetProperty("verified").GetBoolean());
        File.WriteAllText(P("payload.txt"), "changed");
        var invalid = await Call("verify", new { manifest = P("manifest.json"), package = P("source.package") });
        Assert.False(invalid.GetProperty("valid").GetBoolean());

        await Call("project.init", new { path = P("project"), name = "TestProject" });
        File.WriteAllText(P("project/resources/payload.txt"), "<I c=\"Test\" i=\"snippet\" m=\"tests\" n=\"fixture\" s=\"1\"/>");
        Json("project/package.json", new { format = 1, resources = new[] { new { type = "0x03B33DDF", group = "0", instance = "1", source = "resources/payload.txt", encoding = "bytes" } } });
        var projectValidation = await Call("project.validate", new { project = P("project") });
        Assert.True(projectValidation.GetProperty("valid").GetBoolean(), projectValidation.GetRawText());
        var projectBuild = await Call("project.build", new { project = P("project"), outputRoot = P("builds") });
        Assert.True(projectBuild.GetProperty("valid").GetBoolean());
        Assert.True(File.Exists(projectBuild.GetProperty("package").GetString()));
        File.WriteAllText(P("project/package.json"), "{\"format\":1,\"resources\":[{\"type\":\"0\",\"group\":\"0\",\"instance\":\"1\",\"source\":\"missing.bin\",\"encoding\":\"bytes\"}]}");
        Assert.False((await Call("project.validate", new { project = P("project") })).GetProperty("valid").GetBoolean());
        Assert.False((await Call("project.build", new { project = P("project"), outputRoot = P("builds") })).GetProperty("valid").GetBoolean());

        Directory.CreateDirectory(P("game/Data/Client"));
        File.Copy(P("source.package"), P("game/Data/Client/ClientFullBuild0.package"));
        await Call("game.index", new { gameRoot = P("game"), output = P("index.jsonl.gz") });
        Assert.Equal(1, (await Call("game.query", new { index = P("index.jsonl.gz"), tgi })).GetProperty("providers").GetArrayLength());
        Assert.Equal(1, (await Call("game.find", new { index = P("index.jsonl.gz"), type = "0xE882D22F", limit = 1 })).GetProperty("resources").GetArrayLength());
        await Call("project.validate", new { project = P("project"), gameIndex = P("index.jsonl.gz") });
        await Call("conflict.game", new { package = P("source.package"), index = P("index.jsonl.gz") });
        Assert.Equal(4, (await Call("conflict.packages", new { left = P("source.package"), right = P("import.package") })).GetProperty("sharedResourceCount").GetInt32());

        Directory.CreateDirectory(P("tdesc"));
        File.WriteAllText(P("tdesc/test.tdesc"), "<TuningRoot><Instance class=\"Test\" module=\"tests\" instance_type=\"snippet\"><Tunable name=\"value\" type=\"int\"/></Instance></TuningRoot>");
        var snapshot = await Call("tdesc.refresh", new { fromDir = P("tdesc"), gameVersion = "1.2.3.4", source = "generated fixture", output = P("snapshot") });
        Assert.Equal(1, (await Call("tdesc.inspect", new { snapshot = P("snapshot") })).GetProperty("classCount").GetInt32());
        Assert.True((await Call("tdesc.refresh", new { fromDir = P("tdesc"), gameVersion = "1.2.3.4", source = "generated fixture", output = P("snapshot") })).GetProperty("reused").GetBoolean());
        var tuningTgi = "0x03B33DDF:0x00000000:0x0000000000000001";
        File.WriteAllText(P("tuning.xml"), "<I c=\"Test\" i=\"snippet\" m=\"tests\" n=\"fixture\" s=\"1\"><T n=\"value\">7</T></I>");
        var pin = snapshot.GetProperty("sha256").GetString();
        await Call("tuning.validate", new { xml = P("tuning.xml"), tgi = tuningTgi, snapshot = P("snapshot"), gameVersion = "1.2.3.4", snapshotSha256 = pin });
        await Call("tuning.build", new { xml = P("tuning.xml"), tgi = tuningTgi, output = P("tuning.package"), snapshot = P("snapshot"), gameVersion = "1.2.3.4", snapshotSha256 = pin });
        await Call("tuning.export", new { package = P("tuning.package"), tgi = tuningTgi, output = P("export.xml") });
        Assert.Contains("value", File.ReadAllText(P("export.xml")));
        await Call("tuning.import", new { package = P("tuning.package"), xml = P("export.xml"), tgi = tuningTgi, output = P("tuning-import.package"), snapshot = P("snapshot"), gameVersion = "1.2.3.4", snapshotSha256 = pin });
        await Call("tuning.extract", new { package = P("source.package"), combinedTgi, name = "fixture", output = P("extracted.xml") });
        Assert.Contains("fixture", File.ReadAllText(P("extracted.xml")));
        File.WriteAllText(P("bad.xml"), "<combined><g s=\"merged\"><T x=\"0\">1</T></g><I n=\"bad\"><r x=\"99\"/></I></combined>");
        Assert.False((await Call("tuning.validate", new { xml = P("bad.xml") })).GetProperty("valid").GetBoolean());

        var stblTgi = "0x220557DA:0x00000000:0x0000000000000001";
        Json("stbl.json", new { format = 1, tgi = stblTgi, entries = new[] { new { key = "0x12345678", text = "Hello" } } });
        await Call("stbl.build", new { json = P("stbl.json"), output = P("stbl.package") });
        await Call("stbl.export", new { package = P("stbl.package"), tgi = stblTgi, output = P("stbl-export.json") });
        Assert.Contains("Hello", File.ReadAllText(P("stbl-export.json")));
        await Call("stbl.import", new { package = P("stbl.package"), json = P("stbl-export.json"), output = P("stbl-import.package") });
        Assert.Equal("Test", (await Call("casp.inspect", new { package = P("source.package"), tgi = caspTgi })).GetProperty("name").GetString());
        await Call("casp.rename", new { package = P("source.package"), tgi = caspTgi, name = "Next", output = P("casp.package") });
        Assert.Equal("Next", (await Call("casp.inspect", new { package = P("casp.package"), tgi = caspTgi })).GetProperty("name").GetString());
        await Call("simdata.inspect", new { package = P("source.package"), tgi = simdataTgi });
        await Call("simdata.roundtrip", new { package = P("source.package"), tgi = simdataTgi, output = P("simdata-roundtrip.package") });
        var set = await Call("simdata.set", new { package = P("source.package"), tgi = simdataTgi, table = "Values", row = 0, column = "Count", value = "123", output = P("simdata-set.package") });
        Assert.Equal("123", set.GetProperty("newValue").GetString());
        await Call("simdata.inspect", new { package = P("simdata-set.package"), tgi = simdataTgi });

        File.WriteAllBytes(P("image.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg=="));
        var pngTgi = "0x2F7D0004:0x00000000:0x0000000000000001";
        await Call("image.build", new { tgi = pngTgi, png = P("image.png"), output = P("image.package") });
        await Call("image.export", new { package = P("image.package"), tgi = pngTgi, output = P("export.png") });
        Assert.Equal(File.ReadAllBytes(P("image.png")), File.ReadAllBytes(P("export.png")));
        await Call("image.import", new { package = P("image.package"), tgi = pngTgi, png = P("image.png"), output = P("image-import.package") });
        Directory.CreateDirectory(P("bundle"));
        File.Copy(P("image.png"), P("bundle/color.png"));
        File.WriteAllBytes(P("bundle/mask.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGP4DwQACfsD/fteaysAAAAASUVORK5CYII="));
        var rlesTgi = "0xBA856C78:0x00000000:0x0000000000000001";
        Json("bundle/manifest.json", new { schemaVersion = 1, codec = "RLES", sourceTgi = rlesTgi, sourceWidth = 1, sourceHeight = 1, width = 1, height = 1, mipLevel = 0, mipCount = 1, colorFile = "color.png", maskFile = "mask.png", colorFormat = "rgba8", maskFormat = "grayscale8-opaque" });
        await Call("image.build", new { tgi = rlesTgi, bundle = P("bundle"), output = P("rles.package") });
        await Call("image.export", new { package = P("rles.package"), tgi = rlesTgi, bundle = P("export-bundle"), mip = 0 });
        Assert.True(File.Exists(P("export-bundle/mask.png")));
        await Call("image.import", new { package = P("rles.package"), tgi = rlesTgi, bundle = P("export-bundle"), output = P("rles-import.package") });
        async Task Error(string command, object arguments, string code)
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments))!;
            var result = await client.CallToolAsync("s4mt_" + command.Replace('.', '_'), values);
            Assert.True(result.IsError, JsonSerializer.Serialize(result));
            Assert.Equal(code, result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        }
        await Error("inspect", new { package = 7 }, "invalid_arguments");
        await Error("inspect", new { package = P("source.package"), args = "--help" }, "invalid_arguments");
        await Error("image.export", new { package = P("image.package"), tgi = pngTgi, output = P("invalid.png"), mip = "0" }, "invalid_arguments");
        await Error("tuning.validate", new { xml = P("tuning.xml"), snapshot = P("snapshot") }, "invalid_arguments");
        await Error("resource.export", new { package = P("source.package"), tgi, output = P("source.package") }, "path_collision");
        await Error("resource.export", new { package = P("source.package"), tgi, output = cli }, "path_outside_root");
        await Error("resource.export", new { package = P("source.package"), tgi, output = P("game/output.bin") }, "read_only_root");
        File.WriteAllText(P("sentinel.bin"), "preserve");
        File.WriteAllText(P("malformed.xml"), "<broken");
        await Error("tuning.build", new { tgi = tuningTgi, xml = P("malformed.xml"), output = P("sentinel.bin") }, "cli_error");
        Assert.Equal("preserve", File.ReadAllText(P("sentinel.bin")));
        Directory.CreateDirectory(P("unowned-bundle"));
        File.WriteAllText(P("unowned-bundle/keep.txt"), "preserve");
        await Error("image.export", new { package = P("rles.package"), tgi = rlesTgi, bundle = P("unowned-bundle") }, "cli_error");
        Assert.Equal("preserve", File.ReadAllText(P("unowned-bundle/keep.txt")));
        using (S4MT.Mcp.CommandOutputLease.Acquire(P("busy.bin"), false, new S4MT.Mcp.PathPolicy(workspace.Options), []))
            await Error("resource.export", new { package = P("source.package"), tgi, output = P("busy.bin") }, "output_busy");
        var escape = Path.GetFullPath(P("../outside.bin"));
        File.WriteAllText(P("escape.json"), JsonSerializer.Serialize(new { format = 1, Resources = new[] { new { type = "0xE882D22F", group = "0", instance = "1", Source = escape, encoding = "bytes" } } }), new UTF8Encoding(true));
        await Error("build", new { manifest = P("escape.json"), output = P("sentinel.bin") }, "path_outside_root");
        await Error("verify", new { manifest = P("escape.json"), package = P("source.package") }, "path_outside_root");
        Assert.Equal("preserve", File.ReadAllText(P("sentinel.bin")));
        Assert.True((await Call("game.index", new { gameRoot = P("game"), output = P("index.jsonl.gz") })).GetProperty("reused").GetBoolean());
        using var cacheWorkspace = new TestWorkspace();
        var cacheOptions = workspace.Options with { CliPath = cli, GameIndexCacheRoot = cacheWorkspace.Root };
        S4MT.Mcp.GameIndexArtifact artifact;
        using (var cache = new S4MT.Mcp.GameIndexStore(cacheOptions))
        await using (var upload = File.OpenRead(P("index.jsonl.gz")))
            artifact = await cache.ImportAsync(upload, upload.Length, new S4MT.Mcp.CliProcessRunner(cacheOptions), CancellationToken.None);
        workspace.SaveConfig(cacheOptions);
        await using var cachedClient = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = workspace.Dotnet, Arguments = [workspace.Host, "--config", workspace.Configuration]
        }));
        var cached = await cachedClient.CallToolAsync("s4mt_project_validate", new Dictionary<string, object?> { ["project"] = P("project") });
        Assert.NotEqual(true, cached.IsError);
        Assert.Equal(artifact.Sha256, cached.Meta!["gameIndexSha256"]!.GetValue<string>());
        Assert.Equal(artifact.Fingerprint, cached.Meta["gameIndexFingerprint"]!.GetValue<string>());
        var explicitIndex = await cachedClient.CallToolAsync("s4mt_project_validate", new Dictionary<string, object?> { ["project"] = P("project"), ["gameIndex"] = P("index.jsonl.gz") });
        Assert.NotEqual(true, explicitIndex.IsError);
        Assert.False(explicitIndex.Meta!.ContainsKey("gameIndexSha256"));
        Assert.Equal(CommandCatalog.All.Select(command => command.Command).Order(), invoked.Order());
        Assert.Empty(Directory.EnumerateDirectories(workspace.Root, ".s4mt-mcp-stage-*"));
    }
}
