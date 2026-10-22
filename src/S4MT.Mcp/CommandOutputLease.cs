using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using S4MT.Cli;

namespace S4MT.Mcp;

internal sealed class CommandOutputLease : IDisposable
{
    private const string Ready = "S4MT.Mcp output lease v1\n";
    private readonly FileStream gate;
    private readonly string destination;
    private readonly string? stage;
    private readonly PathPolicy policy;
    private bool recoveryBlocked;
    internal string? StagedFile { get; }

    private CommandOutputLease(FileStream gate, string destination, bool tree, PathPolicy policy)
    {
        this.gate = gate;
        this.destination = destination;
        this.policy = policy;
        if (tree) return;
        stage = policy.Write(Path.Combine(Path.GetDirectoryName(destination)!, $".s4mt-mcp-stage-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(stage);
        StagedFile = policy.Write(Path.Combine(stage, Path.GetFileName(destination)));
    }

    internal static CommandOutputLease Acquire(string destination, bool tree, PathPolicy policy, IEnumerable<string> inputs)
    {
        if (tree) policy.CheckTree(destination, write: true);
        var parent = Path.GetDirectoryName(destination)!;
        var canonical = PathGuard.ResolvePath(destination);
        var identity = OperatingSystem.IsWindows() ? canonical.ToUpperInvariant() : canonical;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var lockPath = policy.Write(Path.Combine(parent, $".s4mt-mcp-{hash}.lock"));
        policy.NoCollision(lockPath, inputs);
        Directory.CreateDirectory(parent);
        FileStream gate;
        try { gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new McpFailure("output_busy", "Another MCP invocation owns this output path."); }
        try
        {
            if (gate.Length > 1024) throw new McpFailure("output_not_owned", "Output lock is not an MCP lease.");
            using var reader = new StreamReader(gate, Encoding.UTF8, leaveOpen: true);
            var state = reader.ReadToEnd();
            if (state == Ready + "blocked") throw new McpFailure("cleanup_failed", "Output recovery is blocked because earlier child cleanup could not be confirmed.");
            if (state.Length != 0 && state != Ready) throw new McpFailure("output_not_owned", "Output lock is not an MCP lease.");
            gate.Position = 0;
            gate.Write(Encoding.UTF8.GetBytes(Ready));
            gate.Flush(flushToDisk: true);
            return new CommandOutputLease(gate, destination, tree, policy);
        }
        catch { gate.Dispose(); throw; }
    }

    internal void Publish()
    {
        if (StagedFile is null) return;
        policy.Write(destination);
        File.Move(StagedFile, destination, overwrite: true);
    }

    internal CliReport Rewrite(CliReport report)
    {
        if (StagedFile is null) return report;
        var json = JsonNode.Parse(report.Report.GetRawText())!.AsObject();
        foreach (var name in new[] { "output", "outputPackage", "index" })
            if (json[name] is JsonValue value && value.TryGetValue<string>(out var path) && path == StagedFile)
                json[name] = destination;
        return report with { Report = JsonSerializer.SerializeToElement(json) };
    }

    internal void BlockRecovery()
    {
        recoveryBlocked = true;
        gate.Position = 0;
        gate.Write(Encoding.UTF8.GetBytes(Ready + "blocked"));
        gate.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        try
        {
            if (!recoveryBlocked && stage is not null && Directory.Exists(stage))
            {
                policy.CheckTree(stage, write: true);
                Directory.Delete(stage, recursive: true);
            }
        }
        finally { gate.Dispose(); }
    }
}
