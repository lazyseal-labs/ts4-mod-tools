using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace S4MT.Mcp;

internal sealed record CliReport(JsonElement Report, int ExitCode, string Stderr);

internal sealed class CliProcessRunner
{
    private readonly HostOptions options;
    private readonly Func<int, Process> processResolver;
    private readonly Func<Stream, int, CancellationTokenSource, Task<byte[]>> capture;

    public CliProcessRunner(HostOptions options) : this(options, Process.GetProcessById, CaptureAsync) { }

    internal CliProcessRunner(HostOptions options, Func<int, Process> processResolver) : this(options, processResolver, CaptureAsync) { }

    internal CliProcessRunner(HostOptions options, Func<int, Process> processResolver,
        Func<Stream, int, CancellationTokenSource, Task<byte[]>> capture)
    {
        this.options = options;
        this.processResolver = processResolver;
        this.capture = capture;
    }

    internal static void EnsureSupportedPlatform(bool windows)
    {
        if (!windows)
            throw new McpFailure("cli_unavailable", "Contained CLI execution is currently supported only on Windows.");
    }

    internal async Task<CliReport> RunAsync(IReadOnlyList<string> arguments, string command, CancellationToken cancellationToken, Action<Process>? childStarted = null)
    {
        EnsureSupportedPlatform(OperatingSystem.IsWindows());
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(options.CliPath))
            throw new McpFailure("cli_unavailable", $"Configured CLI does not exist: {options.CliPath}");
        using var job = new ChildProcessJob();
        var isDll = Path.GetExtension(options.CliPath).Equals(".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(isDll ? options.DotnetPath! : options.CliPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(options.CliPath)!
        };
        if (isDll) start.ArgumentList.Add(options.CliPath);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        CliChildProcess invocation;
        try
        {
            invocation = CliChildProcess.Start(start, job, processResolver);
        }
        catch (Exception exception) when (exception is Win32Exception or ArgumentException or InvalidOperationException)
        {
            throw new McpFailure("cli_unavailable", $"Could not start configured CLI: {exception.Message}");
        }
        using var ownedInvocation = invocation;
        var child = invocation.Process;
        try { childStarted?.Invoke(child); }
        catch
        {
            await job.StopAsync(child);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            throw;
        }
        using var timeout = new CancellationTokenSource(options.TimeoutMilliseconds);
        using var exceeded = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, exceeded.Token);
        var stdout = capture(invocation.StandardOutput, options.MaximumStdoutBytes, exceeded);
        var stderr = capture(invocation.StandardError, options.MaximumStderrBytes, exceeded);
        try
        {
            invocation.Resume();
            await Task.WhenAll(child.WaitForExitAsync(), stdout, stderr).WaitAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
        }
        catch (IOException exception)
        {
            await StopAsync(child, job, stdout, stderr);
            throw new McpFailure("cli_io_error", exception.Message);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            await StopAsync(child, job, stdout, stderr);
            throw new McpFailure("cli_unavailable", $"Could not resume configured CLI: {exception.Message}");
        }
        catch (OperationCanceledException)
        {
            await StopAsync(child, job, stdout, stderr);
            var code = cancellationToken.IsCancellationRequested ? "cancelled" : exceeded.IsCancellationRequested ? "output_limit" : "timeout";
            throw new McpFailure(code, code switch
            {
                "cancelled" => "CLI invocation was cancelled. The child was stopped.",
                "output_limit" => "CLI output exceeded the configured capture limit. The child was stopped.",
                _ => "CLI invocation exceeded the configured timeout. The child was stopped."
            });
        }
        await job.StopAsync(child);
        return Decode(await stdout, await stderr, child.ExitCode, command);
    }

    private static async Task<byte[]> CaptureAsync(Stream stream, int limit, CancellationTokenSource exceeded)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            if (output.Length + read > limit)
            {
                exceeded.Cancel();
                break;
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static async Task StopAsync(Process child, ChildProcessJob job, Task<byte[]> stdout, Task<byte[]> stderr)
    {
        try
        {
            await job.StopAsync(child);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or TimeoutException or IOException)
        {
            throw new McpFailure("cleanup_failed", $"Could not confirm child cleanup: {exception.Message}");
        }

        try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException)
        {
            // Process termination is confirmed above; a capture failure cannot imply a surviving child.
        }
    }

    private static CliReport Decode(byte[] stdout, byte[] stderrBytes, int exitCode, string command)
    {
        var stderr = Encoding.UTF8.GetString(stderrBytes);
        if (exitCode == 130) throw new McpFailure("cancelled", "CLI reported cancellation.", exitCode, stderr);
        if (stdout.Length == 0 && exitCode != 0)
            throw new McpFailure("cli_error", "CLI failed. Inspect the bounded stderr report.", exitCode, stderr);
        try
        {
            using var document = JsonDocument.Parse(stdout);
            var report = document.RootElement;
            if (report.ValueKind != JsonValueKind.Object ||
                !report.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
                !report.TryGetProperty("command", out var reportedCommand) || reportedCommand.GetString() != command)
                throw new JsonException("Missing schemaVersion or unexpected report command.");
            var invalid = report.TryGetProperty("valid", out var valid) && valid.ValueKind == JsonValueKind.False;
            if (command == "project.init")
                Require(report, ("projectFile", JsonValueKind.String), ("packageManifest", JsonValueKind.String), ("created", null));
            else
            {
                Require(report, ("diagnostics", JsonValueKind.Array), ("resourceCount", JsonValueKind.Number), ("scriptArchiveCount", JsonValueKind.Number));
                if (command == "project.validate" || invalid) Require(report, ("projectRoot", JsonValueKind.String));
                else Require(report, ("outputDirectory", JsonValueKind.String), ("fingerprint", JsonValueKind.String), ("reused", null));
            }
            if (exitCode == 1 && invalid && command is "project.validate" or "project.build")
                return new CliReport(report.Clone(), exitCode, stderr);
            if (exitCode != 0)
                throw new McpFailure("cli_error", "CLI returned an unsuccessful exit code.", exitCode, stderr);
            if (command != "project.init" && (!report.TryGetProperty("valid", out valid) || valid.ValueKind != JsonValueKind.True))
                throw new JsonException("Successful validation/build must report valid:true.");
            return new CliReport(report.Clone(), exitCode, stderr);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new McpFailure("malformed_cli_output", $"CLI stdout was not the expected JSON report: {exception.Message}", exitCode, stderr);
        }
    }

    private static void Require(JsonElement report, params (string Name, JsonValueKind? Kind)[] fields)
    {
        foreach (var (name, kind) in fields)
            if (!report.TryGetProperty(name, out var value) ||
                (kind is { } expected ? value.ValueKind != expected : value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
                throw new JsonException($"Missing or invalid report field '{name}'.");
    }
}
