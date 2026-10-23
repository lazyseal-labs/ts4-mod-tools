using System.Text.Json;

namespace S4MT.Cli;

internal enum OptionRole { Text, Int, Read, Tree, Manifest, MergeManifest, Project, Bundle, Output, OutputTree }
internal enum ReportKind { String, Number, Boolean, Array, ObjectOrNull, StringOrNull, NumberOrNull }
internal sealed record CommandOption(string Name, string Flag, OptionRole Role, bool Required);
internal sealed record ReportField(string Name, ReportKind Kind);
internal sealed record CommandDescriptor(string Command, CommandOption[] Options, ReportField[] Fields)
{
    internal string ToolName => "s4mt_" + Command.Replace('.', '_');
    internal string[] Words => Command.Split('.');
    internal bool AllowsInvalid => Command is "verify" or "tuning.validate" or "project.validate" or "project.build";
    internal bool ReadOnly => Options.All(option => option.Role is not (OptionRole.Output or OptionRole.OutputTree));
}

internal static class CommandCatalog
{
    internal static readonly CommandDescriptor[] All =
    [
        new("build",
            [new("manifest", "--manifest", OptionRole.Manifest, true), new("output", "--output", OptionRole.Output, true)],
            [new("output", ReportKind.String), new("resourceCount", ReportKind.Number), new("verified", ReportKind.Boolean),
             new("sha256", ReportKind.String)]),
        new("inspect",
            [new("package", "--package", OptionRole.Read, true)],
            [new("package", ReportKind.String), new("resourceCount", ReportKind.Number), new("resources", ReportKind.Array)]),
        new("verify",
            [new("manifest", "--manifest", OptionRole.Manifest, true), new("package", "--package", OptionRole.Read, true)],
            [new("package", ReportKind.String), new("valid", ReportKind.Boolean), new("expectedResourceCount", ReportKind.Number),
             new("actualResourceCount", ReportKind.Number), new("missingKeys", ReportKind.Array), new("extraKeys", ReportKind.Array),
             new("payloadMismatchKeys", ReportKind.Array)]),
        new("package.merge",
            [new("manifest", "--manifest", OptionRole.MergeManifest, true), new("output", "--output", OptionRole.Output, true)],
            [new("outputPackage", ReportKind.String), new("inputPackages", ReportKind.Array), new("resourceCount", ReportKind.Number),
             new("onConflict", ReportKind.String), new("conflicts", ReportKind.Array), new("sha256", ReportKind.String),
             new("verified", ReportKind.Boolean)]),
        new("resource.export",
            [new("package", "--package", OptionRole.Read, true), new("tgi", "--tgi", OptionRole.Text, true),
             new("output", "--output", OptionRole.Output, true)],
            [new("package", ReportKind.String), new("output", ReportKind.String), new("tgi", ReportKind.String),
             new("payloadBytes", ReportKind.Number), new("payloadSha256", ReportKind.String)]),
        new("resource.import",
            [new("package", "--package", OptionRole.Read, true), new("tgi", "--tgi", OptionRole.Text, true),
             new("payload", "--payload", OptionRole.Read, true), new("output", "--output", OptionRole.Output, true)],
            [new("sourcePackage", ReportKind.String), new("outputPackage", ReportKind.String), new("tgi", ReportKind.String),
             new("payloadBytes", ReportKind.Number), new("packageResourceCount", ReportKind.Number), new("payloadSha256", ReportKind.String),
             new("packageSha256", ReportKind.String), new("typedValidation", ReportKind.Boolean), new("verified", ReportKind.Boolean)]),
        new("project.init",
            [new("name", "--name", OptionRole.Text, true), new("path", "--path", OptionRole.OutputTree, true)],
            [new("projectFile", ReportKind.String), new("packageManifest", ReportKind.String), new("created", ReportKind.Boolean)]),
        new("project.validate",
            [new("project", "--project", OptionRole.Project, true), new("gameIndex", "--game-index", OptionRole.Read, false)],
            [new("valid", ReportKind.Boolean), new("diagnostics", ReportKind.Array), new("resourceCount", ReportKind.Number),
             new("scriptArchiveCount", ReportKind.Number), new("projectRoot", ReportKind.String)]),
        new("project.build",
            [new("project", "--project", OptionRole.Project, true), new("outputRoot", "--output-root", OptionRole.OutputTree, true),
             new("gameIndex", "--game-index", OptionRole.Read, false)],
            [new("valid", ReportKind.Boolean), new("diagnostics", ReportKind.Array), new("resourceCount", ReportKind.Number),
             new("scriptArchiveCount", ReportKind.Number)]),
        new("game.index",
            [new("gameRoot", "--game-root", OptionRole.Tree, true), new("output", "--output", OptionRole.Output, true)],
            [new("index", ReportKind.String), new("gameRoot", ReportKind.String), new("gameVersion", ReportKind.String),
             new("packageCount", ReportKind.Number), new("resourceCount", ReportKind.Number), new("fingerprint", ReportKind.String),
             new("reused", ReportKind.Boolean), new("precedenceVerified", ReportKind.Boolean)]),
        new("game.query",
            [new("index", "--index", OptionRole.Read, true), new("tgi", "--tgi", OptionRole.Text, true)],
            [new("index", ReportKind.String), new("tgi", ReportKind.String), new("resolution", ReportKind.String),
             new("precedenceVerified", ReportKind.Boolean), new("uniqueProvider", ReportKind.ObjectOrNull), new("providers", ReportKind.Array)]),
        new("game.find",
            [new("index", "--index", OptionRole.Read, true), new("type", "--type", OptionRole.Text, true),
             new("limit", "--limit", OptionRole.Int, false)],
            [new("index", ReportKind.String), new("type", ReportKind.String), new("matchCount", ReportKind.Number),
             new("resources", ReportKind.Array), new("gameVersion", ReportKind.String), new("fingerprint", ReportKind.String),
             new("packageCount", ReportKind.Number), new("resourceCount", ReportKind.Number), new("generatedUtc", ReportKind.String)]),
        new("tdesc.refresh",
            [new("fromDir", "--from-dir", OptionRole.Tree, true), new("gameVersion", "--game-version", OptionRole.Text, true),
             new("source", "--source", OptionRole.Text, true), new("output", "--output", OptionRole.OutputTree, true)],
            [new("snapshot", ReportKind.String), new("gameVersion", ReportKind.String), new("source", ReportKind.String),
             new("sourceFileCount", ReportKind.Number), new("classCount", ReportKind.Number), new("sha256", ReportKind.String),
             new("reused", ReportKind.Boolean)]),
        new("tdesc.inspect",
            [new("snapshot", "--snapshot", OptionRole.Tree, true)],
            [new("snapshot", ReportKind.String), new("gameVersion", ReportKind.String), new("source", ReportKind.String),
             new("importedUtc", ReportKind.String), new("sourceFileCount", ReportKind.Number), new("classCount", ReportKind.Number),
             new("sha256", ReportKind.String), new("classes", ReportKind.Array)]),
        new("stbl.build",
            [new("json", "--json", OptionRole.Read, true), new("output", "--output", OptionRole.Output, true)],
            [new("outputPackage", ReportKind.String), new("tgi", ReportKind.String), new("locale", ReportKind.String),
             new("entryCount", ReportKind.Number), new("sha256", ReportKind.String), new("verified", ReportKind.Boolean)]),
        new("stbl.export",
            [new("package", "--package", OptionRole.Read, true), new("tgi", "--tgi", OptionRole.Text, true),
             new("output", "--output", OptionRole.Output, true)],
            [new("package", ReportKind.String), new("output", ReportKind.String), new("tgi", ReportKind.String), new("locale", ReportKind.String),
             new("entryCount", ReportKind.Number)]),
        new("stbl.import",
            [new("package", "--package", OptionRole.Read, true), new("json", "--json", OptionRole.Read, true),
             new("output", "--output", OptionRole.Output, true)],
            [new("sourcePackage", ReportKind.String), new("outputPackage", ReportKind.String), new("tgi", ReportKind.String),
             new("locale", ReportKind.String), new("entryCount", ReportKind.Number), new("packageResourceCount", ReportKind.Number),
             new("sha256", ReportKind.String), new("verified", ReportKind.Boolean)]),
        new("casp.inspect",
            [new("package", "--package", OptionRole.Read, true), new("tgi", "--tgi", OptionRole.Text, true)],
            [new("package", ReportKind.String), new("tgi", ReportKind.String), new("version", ReportKind.Number), new("name", ReportKind.String),
             new("nameByteLength", ReportKind.Number), new("payloadBytes", ReportKind.Number)]),
        new("casp.rename",
            [new("package", "--package", OptionRole.Read, true), new("tgi", "--tgi", OptionRole.Text, true),
             new("name", "--name", OptionRole.Text, true), new("output", "--output", OptionRole.Output, true)],
            [new("sourcePackage", ReportKind.String), new("outputPackage", ReportKind.String), new("tgi", ReportKind.String),
             new("version", ReportKind.Number), new("previousName", ReportKind.String), new("name", ReportKind.String),
             new("payloadBytes", ReportKind.Number), new("packageResourceCount", ReportKind.Number), new("sha256", ReportKind.String),
             new("verified", ReportKind.Boolean)]),
        new("tuning.extract",
            [new("package", "--package", OptionRole.Read, true), new("combinedTgi", "--combined-tgi", OptionRole.Text, true),
             new("name", "--name", OptionRole.Text, true), new("output", "--output", OptionRole.Output, true)],
            [new("package", ReportKind.String), new("output", ReportKind.String), new("name", ReportKind.String),
             new("tuningClass", ReportKind.StringOrNull), new("module", ReportKind.StringOrNull), new("tgi", ReportKind.String),
             new("extractedElementCount", ReportKind.Number)]),
        new("tuning.build",
            [new("tgi", "--tgi", OptionRole.Text, true), new("xml", "--xml", OptionRole.Read, true),
             new("output", "--output", OptionRole.Output, true), new("snapshot", "--snapshot", OptionRole.Tree, false),
             new("gameVersion", "--game-version", OptionRole.Text, false), new("snapshotSha256", "--snapshot-sha256", OptionRole.Text, false)],
            [new("outputPackage", ReportKind.String), new("tgi", ReportKind.String), new("rootName", ReportKind.String),
             new("sha256", ReportKind.String), new("verified", ReportKind.Boolean)]),
        new("tuning.validate",
            [new("xml", "--xml", OptionRole.Read, true), new("tgi", "--tgi", OptionRole.Text, false),
             new("snapshot", "--snapshot", OptionRole.Tree, false), new("gameVersion", "--game-version", OptionRole.Text, false),
             new("snapshotSha256", "--snapshot-sha256", OptionRole.Text, false)],
            [new("xml", ReportKind.String), new("wellFormed", ReportKind.Boolean), new("valid", ReportKind.Boolean)]),
        new("tuning.export",
            [new("package", "--package", OptionRole.Read, true), new("tgi", "--tgi", OptionRole.Text, true),
             new("output", "--output", OptionRole.Output, true)],
            [new("package", ReportKind.String), new("output", ReportKind.String), new("tgi", ReportKind.String), new("rootName", ReportKind.String)]),
        new("tuning.import",
            [new("package", "--package", OptionRole.Read, true), new("tgi", "--tgi", OptionRole.Text, true),
             new("xml", "--xml", OptionRole.Read, true), new("output", "--output", OptionRole.Output, true),
             new("snapshot", "--snapshot", OptionRole.Tree, false), new("gameVersion", "--game-version", OptionRole.Text, false),
             new("snapshotSha256", "--snapshot-sha256", OptionRole.Text, false)],
            [new("sourcePackage", ReportKind.String), new("outputPackage", ReportKind.String), new("tgi", ReportKind.String),
             new("rootName", ReportKind.String), new("packageResourceCount", ReportKind.Number), new("sha256", ReportKind.String),
             new("verified", ReportKind.Boolean)]),
        new("simdata.inspect",
            [new("package", "--package", OptionRole.Read, true), new("tgi", "--tgi", OptionRole.Text, true)],
            [new("package", ReportKind.String), new("tgi", ReportKind.String), new("resourceName", ReportKind.StringOrNull),
             new("version", ReportKind.String), new("tableCount", ReportKind.Number), new("rowCount", ReportKind.Number),
             new("tables", ReportKind.Array)]),
        new("simdata.roundtrip",
            [new("package", "--package", OptionRole.Read, true), new("tgi", "--tgi", OptionRole.Text, true),
             new("output", "--output", OptionRole.Output, true)],
            [new("sourcePackage", ReportKind.String), new("outputPackage", ReportKind.String), new("tgi", ReportKind.String),
             new("resourceName", ReportKind.StringOrNull), new("version", ReportKind.String), new("tableCount", ReportKind.Number),
             new("rowCount", ReportKind.Number), new("sha256", ReportKind.String), new("verified", ReportKind.Boolean)]),
        new("simdata.set",
            [new("package", "--package", OptionRole.Read, true), new("tgi", "--tgi", OptionRole.Text, true),
             new("table", "--table", OptionRole.Text, true), new("row", "--row", OptionRole.Int, true),
             new("column", "--column", OptionRole.Text, true), new("value", "--value", OptionRole.Text, true),
             new("output", "--output", OptionRole.Output, true)],
            [new("sourcePackage", ReportKind.String), new("outputPackage", ReportKind.String), new("tgi", ReportKind.String),
             new("resourceName", ReportKind.StringOrNull), new("version", ReportKind.String), new("table", ReportKind.String),
             new("row", ReportKind.Number), new("column", ReportKind.String), new("type", ReportKind.String), new("oldValue", ReportKind.String),
             new("newValue", ReportKind.String), new("packageResourceCount", ReportKind.Number), new("sha256", ReportKind.String),
             new("verified", ReportKind.Boolean)]),
        new("image.export",
            [new("package", "--package", OptionRole.Read, true), new("tgi", "--tgi", OptionRole.Text, true),
             new("output", "--output", OptionRole.Output, false), new("bundle", "--bundle", OptionRole.OutputTree, false),
             new("mip", "--mip", OptionRole.Int, false)],
            [new("package", ReportKind.String), new("tgi", ReportKind.String), new("codec", ReportKind.String), new("width", ReportKind.Number),
             new("height", ReportKind.Number), new("mipLevel", ReportKind.NumberOrNull), new("mipCount", ReportKind.NumberOrNull)]),
        new("image.build",
            [new("tgi", "--tgi", OptionRole.Text, true), new("png", "--png", OptionRole.Read, false),
             new("bundle", "--bundle", OptionRole.Bundle, false), new("output", "--output", OptionRole.Output, true)],
            [new("outputPackage", ReportKind.String), new("tgi", ReportKind.String), new("width", ReportKind.Number),
             new("height", ReportKind.Number), new("sha256", ReportKind.String), new("readBackVerified", ReportKind.Boolean)]),
        new("image.import",
            [new("package", "--package", OptionRole.Read, true), new("tgi", "--tgi", OptionRole.Text, true),
             new("png", "--png", OptionRole.Read, false), new("bundle", "--bundle", OptionRole.Bundle, false),
             new("output", "--output", OptionRole.Output, true)],
            [new("sourcePackage", ReportKind.String), new("outputPackage", ReportKind.String), new("tgi", ReportKind.String),
             new("width", ReportKind.Number), new("height", ReportKind.Number), new("sha256", ReportKind.String),
             new("readBackVerified", ReportKind.Boolean)]),
        new("conflict.game",
            [new("package", "--package", OptionRole.Read, true), new("index", "--index", OptionRole.Read, true)],
            [new("package", ReportKind.String), new("index", ReportKind.String), new("packageResourceCount", ReportKind.Number),
             new("conflictingResourceCount", ReportKind.Number), new("precedenceVerified", ReportKind.Boolean), new("conflicts", ReportKind.Array)]),
        new("conflict.packages",
            [new("left", "--left", OptionRole.Read, true), new("right", "--right", OptionRole.Read, true)],
            [new("leftPackage", ReportKind.String), new("rightPackage", ReportKind.String), new("leftResourceCount", ReportKind.Number),
             new("rightResourceCount", ReportKind.Number), new("sharedResourceCount", ReportKind.Number), new("sharedTgis", ReportKind.Array)]),
    ];

    internal static CommandDescriptor Get(string command) => All.Single(item => item.Command == command);

    internal static void ValidateInvocation(string[] arguments)
    {
        var command = All.SingleOrDefault(item => arguments.Take(item.Words.Length).SequenceEqual(item.Words));
        if (command is null) throw new CliInputException("Unknown operational command.");
        var flags = command.Options.Select(option => option.Flag).ToHashSet(StringComparer.Ordinal);
        for (var index = command.Words.Length; index < arguments.Length; index += 2)
            if (!flags.Contains(arguments[index]))
                throw new CliInputException($"Unknown option '{arguments[index]}'.");
    }

    internal static void ValidateReport(string command, JsonElement report)
    {
        var descriptor = Get(command);
        foreach (var field in descriptor.Fields)
        {
            if (!report.TryGetProperty(field.Name, out var value) || !(field.Kind switch
            {
                ReportKind.String => value.ValueKind == JsonValueKind.String,
                ReportKind.Number => value.ValueKind == JsonValueKind.Number,
                ReportKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                ReportKind.StringOrNull => value.ValueKind is JsonValueKind.String or JsonValueKind.Null,
                ReportKind.NumberOrNull => value.ValueKind is JsonValueKind.Number or JsonValueKind.Null,
                ReportKind.ObjectOrNull => value.ValueKind is JsonValueKind.Object or JsonValueKind.Null,
                ReportKind.Array => value.ValueKind == JsonValueKind.Array,
                _ => false
            })) throw new JsonException($"Missing or invalid report field '{field.Name}'.");
        }
        if (command == "project.build")
        {
            var invalid = report.GetProperty("valid").ValueKind == JsonValueKind.False;
            Require(report, invalid ? "projectRoot" : "outputDirectory", JsonValueKind.String);
            if (!invalid)
            {
                Require(report, "fingerprint", JsonValueKind.String);
                Require(report, "reused", null);
            }
        }
        if (command == "image.export")
        {
            if (report.GetProperty("codec").GetString() == "rles")
                foreach (var field in new[] { "bundle", "colorPng", "maskPng" }) Require(report, field, JsonValueKind.String);
            else Require(report, "output", JsonValueKind.String);
        }
    }

    private static void Require(JsonElement report, string name, JsonValueKind? kind)
    {
        if (!report.TryGetProperty(name, out var value) ||
            (kind is { } expected ? value.ValueKind != expected : value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
            throw new JsonException($"Missing or invalid report field '{name}'.");
    }
}
