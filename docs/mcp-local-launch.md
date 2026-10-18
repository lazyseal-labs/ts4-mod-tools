# Run the project tools over stdio

Build the solution with .NET 10.

```powershell
dotnet build S4MT.sln -c Release
```

Create a local configuration file. Use absolute paths for your checkout, the .NET host, and your disposable authoring directories.

```json
{
  "cliPath": "C:/work/s4mt/src/S4MT.Cli/bin/Release/net10.0/s4mt.dll",
  "dotnetPath": "C:/Program Files/dotnet/dotnet.exe",
  "readRoots": ["C:/work/mod-authoring"],
  "writeRoots": ["C:/work/mod-authoring"],
  "gameRoots": ["C:/Program Files/EA Games/The Sims 4"],
  "timeoutMilliseconds": 30000,
  "maximumStdoutBytes": 1048576,
  "maximumStderrBytes": 65536
}
```

Launch the built host with these command arguments in your stdio client.

```powershell
& 'C:/Program Files/dotnet/dotnet.exe' `
  'C:/work/s4mt/src/S4MT.Mcp/bin/Release/net10.0/S4MT.Mcp.dll' `
  --config 'C:/work/mcp-config.json'
```

Use the built DLL directly. `dotnet run` can add build output to protocol stdout. The host writes diagnostics to stderr. Configuration contains the trusted CLI entrypoint. A native CLI executable can replace the DLL and does not need `dotnetPath`.

The host discovers exactly three tools.

- `s4mt_project_init` takes `path` and `name`. Directory paths can contain spaces. The package name must start with an ASCII letter or digit and contain only ASCII letters, digits, dots, underscores, or hyphens. Its maximum length is 64.
- `s4mt_project_validate` takes `project` and an optional `gameIndex`. A project can be a directory or an explicit project document filename.
- `s4mt_project_build` takes `project`, `outputRoot`, and an optional `gameIndex`. Use an empty directory dedicated to MCP build output on first use. Later calls require the ownership marker. Builds sharing that root hold an exclusive lock.

All tool paths are explicit and absolute. Indirect manifests, resource sources, scripts, and schema snapshots must remain inside their CLI containment boundaries and configured read roots. Symbolic links and junctions receive the same resolved-path checks as the CLI. Existing output descendants also receive checks before reuse. Game roots and the active Mods directory are read-only. Keep authoring and output directories separate from game installation and user data.

Results contain the full CLI JSON in `structuredContent` and a short text summary. `_meta.cliExitCode` and `_meta.cliStderr` retain process details. Unknown report fields, diagnostics, warnings, and schema versions survive unchanged. Exit 1 with `valid:false` is a normal validation or build report. Verification describes package bytes and supported validators. It does not prove game behavior.

Stable tool error codes include `invalid_arguments`, `path_outside_root`, `read_only_root`, `unsafe_project_path`, `path_collision`, `input_limit`, `output_not_owned`, `output_busy`, `cli_unavailable`, `cli_error`, `malformed_cli_output`, `output_limit`, `timeout`, `cancelled`, `cli_io_error`, `cleanup_failed`, and `file_access_error`. CLI failures retain the exit code and bounded stderr. Metadata files are limited to 1 MiB, and checked directory trees are limited to 20,000 entries.

Cancellation and timeout stop and reap the child process tree. On Windows, process jobs also contain descendants after their parent exits and stop them when the host dies. Under the output lock, the host removes GUID staging directories only from its dedicated marked output root. Output trees cannot contain linked files or directories. A saved child PID and start time prevent recovery while an earlier child is still active. Retry a cancelled build to reuse any complete verified output. Retry init with the same path and name to repair an interrupted scaffold. A `cleanup_failed` marker blocks automatic recovery. Preserve that root for inspection and use a fresh empty output root. Do not edit project files, links, or the dedicated output root during a request. Root checks protect static local paths and do not provide an OS sandbox against concurrent hostile filesystem changes.

Run the checked-in stdio client to verify discovery and a project workflow. It saves the actual protocol messages, stderr, and host exit code.

```powershell
python scripts/verify-mcp-stdio.py `
  --host src/S4MT.Mcp/bin/Release/net10.0/S4MT.Mcp.dll `
  --config C:/work/mcp-config.json `
  --project C:/work/mod-authoring/MyProject `
  --output-root C:/work/mod-authoring/builds `
  --transcript .audit/mcp1/stdio.json
dotnet test S4MT.sln
```

The automated tests also use the official C# SDK stdio client. Clients must send the MCP `notifications/cancelled` message to cancel server work. Cancelling a local response wait alone does not prove server cancellation. The SDK can suppress the response after that notification, so verify child termination and the next successful request. No specific desktop client UI is claimed as verified.
