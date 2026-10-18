using System.Diagnostics;
using System.Text.Json;

var root = AppContext.BaseDirectory;
if (args is ["child"])
{
    File.WriteAllText(Path.Combine(root, "descendant.pid"), Environment.ProcessId.ToString());
    await Task.Delay(60_000);
    return 0;
}
var mode = File.ReadAllText(Path.Combine(root, "mode.txt"));
File.WriteAllText(Path.Combine(root, "child.pid"), Environment.ProcessId.ToString());
File.WriteAllText(Path.Combine(root, "arguments.json"), JsonSerializer.Serialize(args));
if (mode is "wait" or "orphan")
{
    var outputIndex = Array.IndexOf(args, "--output-root");
    if (outputIndex >= 0)
    {
        var stage = Path.Combine(args[outputIndex + 1], $".s4mt-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "partial.package"), "partial");
    }
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add(typeof(Program).Assembly.Location);
    start.ArgumentList.Add("child");
    using var descendant = Process.Start(start);
    if (mode == "orphan") return 0;
    await Task.Delay(60_000);
}
if (mode == "malformed") { Console.WriteLine("not JSON"); return 0; }
if (mode == "array") { Console.WriteLine("[]"); return 0; }
if (mode == "error") { Console.Error.WriteLine("{\"schemaVersion\":1,\"error\":{\"code\":\"invalid_input\",\"message\":\"fixture error\"}}"); return 2; }
if (mode == "flood") { Console.Write(new string('x', 131_072)); return 0; }
if (mode == "stderr-flood") { Console.Error.Write(new string('x', 131_072)); return 1; }
var command = $"project.{args[1]}";
Console.WriteLine(JsonSerializer.Serialize(new
{
    schemaVersion = 1,
    command,
    valid = mode != "invalid",
    projectRoot = root,
    projectFile = Path.Combine(root, "s4mt.json"),
    packageManifest = Path.Combine(root, "package.json"),
    created = true,
    resourceCount = 0,
    scriptArchiveCount = 0,
    outputDirectory = root,
    fingerprint = new string('0', 64),
    reused = false,
    arguments = args,
    diagnostics = new[] { new { code = "fixture", path = "source.xml", futureDiagnostic = new { retained = true } } },
    futureReport = new { retained = new[] { "unknown", "fields" } }
}));
return mode == "invalid" ? 1 : 0;
