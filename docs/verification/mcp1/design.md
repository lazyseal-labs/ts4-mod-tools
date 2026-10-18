# MCP-1 adapter design

The host exposes three project operations. It calls the existing CLI in a child process and returns its complete JSON report. The CLI owns project validation, package verification, fingerprints, and reuse decisions.

The data shapes are `HostOptions`, a complete `CliReport` JSON value, and `McpFailure` with a stable code and optional CLI exit code and stderr. `ProjectTools` owns the fixed argument mapping. `PathPolicy` owns the supported project input graph and configured root policy. `CliProcessRunner` owns bounded concurrent stream capture and child lifetime. `BuildOutputLease` owns a dedicated output directory, its cross-process lock, and staging recovery.

Three independent designs were compared. Candidate A used original paths and a dedicated output root. Candidate B copied projects into private workspaces and published their results. Candidate C used typed report records and direct root checks. The independent cross-judge scored A 17/20, B 13/20, and C 11/20 against report fidelity, path safety, cleanup ownership, and interface size. All configured judge-pool models had also run a candidate. The judge disclosed that overlap.

Candidate A is the base. The host adds Candidate B's indirect snapshot checks and saved child identity. Full project copying was rejected because it would change report paths and add publication logic. Typed report deserialization was rejected because nested unknown fields could disappear. The host compiles the existing CLI `PathGuard.cs` source into its own assembly, so it uses the same collision and Windows final-path checks without modifying the CLI.

Build output must be empty on first use or already marked for MCP ownership. This trades shared arbitrary output directories for bounded recovery after forceful process termination. Init keeps the CLI's write-if-absent repair behavior. A retry converges on the same scaffold. Static path checks do not isolate requests from hostile concurrent filesystem changes.

The owner writes all implementation in the assigned existing worktree. Design and review delegates inspect only, because the operator forbids writes in other checkouts. The repeatable stdio client, fault-injection child, and behavior tests provide independent ways to exercise the protocol boundary.
