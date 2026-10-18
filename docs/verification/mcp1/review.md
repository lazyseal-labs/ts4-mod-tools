# MCP-1 review record

The intent is a local stdio adapter for the existing CLI project workflow. The host preserves report JSON and limits tools to project init, validate, and build. It adds configured roots, child-process limits, and bounded recovery. Package installation, game launch, generic execution, and later MCP tools remain outside this change.

The first panel reviewed the complete new host, tests, and verification code against base `4061bd2d2f756c9be57c0c78738f4ee3518f48bd`. Reviewer A ran on `codex/gpt-6-luna` with maximum reasoning. Reviewer B ran on `codex/gpt-6.1-sol` with medium reasoning. Reviewer C ran on `antigravity/gemini-3.8-flash-high`. The files were still uncommitted during this first pass.

Accepted findings and fixes follow.

- B found case-sensitive and BOM handling differences between preflight and the CLI. Preflight now reads text with BOM detection and uses the last case-insensitive property match. Stdio regressions protect game roots and input files from cleanup with mixed-case and BOM project documents.
- B found that process-tree cleanup lost descendants after the immediate child exited. Windows jobs now own invocation descendants and host lifetime. Tests cover parent-first exit, cancellation, and abrupt host death followed by recovery.
- A found a possible dangling-link metadata write. Marker writes now use unique `CreateNew` temporary files and atomic replacement. Path checks reject dangling links before lock and metadata operations.
- A found that a copied marker could authorize staging deletion in another root. Markers now bind to the output directory. Cleanup journals the stage names from a recorded invocation. An idle root rejects unknown stage directories. Regression tests preserve unrelated data under copied and idle markers.
- C found that stale process inspection could throw an unhandled platform exception. Uncertain process state now returns `output_busy` and preserves staging.
- C found linked staging traversal before rejection. Write-tree checks now reject linked files and directories before traversal. A regression verifies that linked staging and its target remain intact.

Two accepted nits removed a duplicate snapshot input entry and made test .NET host discovery follow the active runtime installation. The suggested use of `Environment.ProcessPath` was not taken because a test host can be an executable other than `dotnet`.

The no-comments pass ran on `codex/gpt-6-luna`. It found no comment removals or application changes. The owner ran deslop over the complete diff, then checked whitespace and behavior. No review gate was waived. Final fix review and committed-head evidence are recorded separately when complete.
