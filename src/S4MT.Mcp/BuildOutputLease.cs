using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;

namespace S4MT.Mcp;

internal sealed class BuildOutputLease : IDisposable
{
    private const string MarkerName = ".s4mt-mcp-owner.json";
    private readonly string root;
    private readonly PathPolicy policy;
    private readonly FileStream gate;
    private Owner state;

    private BuildOutputLease(string root, PathPolicy policy, FileStream gate)
    {
        this.root = root;
        this.policy = policy;
        this.gate = gate;
        state = new Owner(1, "S4MT.Mcp", root, null, null, [], false);
    }

    internal static BuildOutputLease Acquire(string root, PathPolicy policy)
    {
        policy.CheckTree(root, write: true);
        Directory.CreateDirectory(root);
        var lockPath = policy.Write(Path.Combine(root, ".s4mt-mcp.lock"));
        FileStream gate;
        try { gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new McpFailure("output_busy", "Another MCP build owns this output root."); }
        var lease = new BuildOutputLease(root, policy, gate);
        try
        {
            var markerPath = Path.Combine(root, MarkerName);
            if (!File.Exists(markerPath))
            {
                if (Directory.EnumerateFileSystemEntries(root).Any(path => Path.GetFileName(path) != ".s4mt-mcp.lock"))
                    throw new McpFailure("output_not_owned", "Use an empty output root dedicated to MCP builds.");
                lease.WriteMarker(null);
            }
            else
            {
                if (new FileInfo(markerPath).Length > 1_048_576)
                    throw new McpFailure("output_not_owned", "MCP ownership marker exceeds its metadata limit.");
                Owner? marker;
                try { marker = JsonSerializer.Deserialize<Owner>(File.ReadAllText(markerPath)); }
                catch (JsonException) { throw new McpFailure("output_not_owned", "Output root has an invalid MCP ownership marker."); }
                if (marker is null || marker.Format != 1 || marker.OwnerName != "S4MT.Mcp" ||
                    string.IsNullOrWhiteSpace(marker.RootPath) || !Path.IsPathFullyQualified(marker.RootPath) ||
                    !PathPolicy.Within(root, marker.RootPath) || !PathPolicy.Within(marker.RootPath, root) ||
                    (marker.ProcessId is null) != (marker.StartTimeTicks is null) ||
                    marker.ProcessId is <= 0 || marker.StartTimeTicks is <= 0 ||
                    marker.StagingDirectories is null || marker.StagingDirectories.Any(name => !IsStageName(name)))
                    throw new McpFailure("output_not_owned", "Output root has an invalid MCP ownership marker.");
                lease.state = marker;
                if (marker.RecoveryBlocked)
                    throw new McpFailure("cleanup_failed", "Output recovery is blocked because earlier process cleanup could not be confirmed.");
                if (marker.ProcessId is { } pid)
                {
                    try
                    {
                        using var child = Process.GetProcessById(pid);
                        if (!child.HasExited && child.StartTime.ToUniversalTime().Ticks == marker.StartTimeTicks)
                            throw new McpFailure("output_busy", "A previous CLI child is still running in this output root.");
                    }
                    catch (ArgumentException) { }
                    catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                    {
                        throw new McpFailure("output_busy", "Cannot confirm whether the previous CLI child has stopped.");
                    }
                }
            }
            lease.CleanStages();
            lease.WriteMarker(null);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal void ChildStarted(Process process) => WriteMarker(process);
    internal void BlockRecovery()
    {
        var stages = state.StagingDirectories.Concat(EnumerateStageNames()).Distinct(StringComparer.Ordinal).ToArray();
        PersistMarker(state with { StagingDirectories = stages, RecoveryBlocked = true });
    }

    internal void Complete()
    {
        CleanStages();
        WriteMarker(null);
    }

    private void CleanStages()
    {
        var names = EnumerateStageNames();
        if (state.ProcessId is not null) WriteMarker(null, stages: names);
        if (names.Any(name => !state.StagingDirectories.Contains(name, StringComparer.Ordinal)))
            throw new McpFailure("output_not_owned", "Output root contains staging data without a recorded MCP invocation.");
        foreach (var name in state.StagingDirectories)
        {
            var stage = Path.Combine(root, name);
            if (!Directory.Exists(stage)) continue;
            if ((File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0)
                throw new McpFailure("unsafe_project_path", "Refusing to clean a linked staging directory.");
            policy.CheckTree(stage, write: true);
            Directory.Delete(stage, recursive: true);
        }
    }

    private static bool IsStageName(string name) => name.StartsWith(".s4mt-stage-", StringComparison.Ordinal) &&
        Guid.TryParseExact(name[".s4mt-stage-".Length..], "N", out _);

    private void WriteMarker(Process? process, string[]? stages = null)
    {
        PersistMarker(new Owner(1, "S4MT.Mcp", root, process?.Id, process?.StartTime.ToUniversalTime().Ticks, stages ?? []));
    }

    private string[] EnumerateStageNames() => Directory.EnumerateDirectories(root, ".s4mt-stage-*")
        .Select(path => Path.GetRelativePath(root, path)).Where(IsStageName).ToArray();

    private void PersistMarker(Owner marker)
    {
        var path = Path.Combine(root, MarkerName);
        var temporary = Path.Combine(root, $".s4mt-mcp-owner-{Guid.NewGuid():N}.tmp");
        policy.Write(temporary);
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(file, marker);
            File.Move(temporary, path, overwrite: true);
            state = marker;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Dispose() => gate.Dispose();
    private sealed record Owner(int Format, string OwnerName, string RootPath, int? ProcessId, long? StartTimeTicks,
        string[] StagingDirectories, bool RecoveryBlocked = false);
}
