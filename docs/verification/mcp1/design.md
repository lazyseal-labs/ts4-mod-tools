# MCP-1 adapter design

The host exposes three project operations. It calls the existing CLI in a child process and returns its complete JSON report. The CLI owns project validation, package verification, fingerprints, and reuse decisions.

The data shapes are `HostOptions`, a complete `CliReport` JSON value, and `McpFailure` with a stable code and optional CLI exit code and stderr. `ProjectTools` owns the fixed argument mapping. `PathPolicy` owns the supported project input graph and configured root policy. `CliProcessRunner` owns bounded concurrent stream capture and child lifetime. `BuildOutputLease` owns a dedicated output directory, its cross-process lock, and staging recovery.

Three independent designs were compared. Candidate A used original paths and a dedicated output root. Candidate B copied projects into private workspaces and published their results. Candidate C used typed report records and direct root checks. The independent cross-judge scored A 17/20, B 13/20, and C 11/20 against report fidelity, path safety, cleanup ownership, and interface size. All configured judge-pool models had also run a candidate. The judge disclosed that overlap.

Candidate A is the base. The host adds Candidate B's indirect snapshot checks and saved child identity. Full project copying was rejected because it would change report paths and add publication logic. Typed report deserialization was rejected because nested unknown fields could disappear. The host compiles the existing CLI `PathGuard.cs` source into its own assembly, so it uses the same collision and Windows final-path checks without modifying the CLI.

Build output must be empty on first use or already marked for MCP ownership. This trades shared arbitrary output directories for bounded recovery after forceful process termination. Init keeps the CLI's write-if-absent repair behavior. A retry converges on the same scaffold. Static path checks do not isolate requests from hostile concurrent filesystem changes.

Contained CLI execution is currently Windows-only. The three MCP tools return `cli_unavailable` on other platforms before project path checks or output-root recovery. The non-Windows fallback cannot prove that descendants have stopped after the CLI parent exits, so it does not launch the CLI or clean an existing output root.

## Windows child launch

`CliProcessRunner` uses `CreateProcessW` with `PROC_THREAD_ATTRIBUTE_JOB_LIST` and `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`. The job list assigns the CLI to its invocation job during process creation. The handle list passes the stdin, stdout, and stderr pipe handles that the CLI needs. `CREATE_SUSPENDED` keeps the primary thread from running while the host writes the build marker. The host resumes the thread after that write succeeds. This lets recovery identify the CLI process before it can create staging data.

`ProcessStartInfo.ArgumentList` remains the argument source. `CliChildProcess` encodes those arguments into a mutable Win32 command line immediately before [`CreateProcessW`](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw), whose documentation says it may modify the `lpCommandLine` buffer.

## Recovery when cleanup cannot be confirmed

When the marker has `RecoveryBlocked:true`, `BuildOutputLease.Acquire` returns `cleanup_failed` and leaves the marker and staging data unchanged. The host does not clear this state automatically because it could not confirm that the previous CLI process stopped. Deleting staging data while that process may still write could discard its output.

Setting `RecoveryBlocked` preserves the known child PID and start time and records the stage names present at the time of failure. A malformed marker or a marker with only one half of the process identity is rejected as `output_not_owned` before stage cleanup.

To resume build work, stop the MCP host that reported `cleanup_failed` and set `outputRoot` to a new empty directory. Keep the blocked output root intact until you have reviewed or preserved its contents. The staging-crash receipt starts a real host against a seeded `RecoveryBlocked:true` marker. The host returns `cleanup_failed` and leaves the stage sentinel unchanged.

The owner writes all implementation in the assigned existing worktree. Design and review delegates inspect only, because the operator forbids writes in other checkouts. The repeatable stdio client, fault-injection child, and behavior tests provide independent ways to exercise the protocol boundary.
