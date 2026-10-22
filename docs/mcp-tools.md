# MCP tools

Both stdio and HTTP expose all 32 operational CLI commands. Tool names use `s4mt_` followed by the CLI words separated by underscores. Help and version remain CLI-only. All parameters are strings except `limit`, `mip`, and `row`, which are JSON integers. A trailing `?` below means optional. Optional values use the CLI default when omitted, except that project `gameIndex` uses the active server cache when configured.

| Tool | Parameters |
| --- | --- |
| `s4mt_build` | `manifest`, `output` |
| `s4mt_inspect` | `package` |
| `s4mt_verify` | `manifest`, `package` |
| `s4mt_package_merge` | `manifest`, `output` |
| `s4mt_resource_export` | `package`, `tgi`, `output` |
| `s4mt_resource_import` | `package`, `tgi`, `payload`, `output` |
| `s4mt_project_init` | `name`, `path` |
| `s4mt_project_validate` | `project`, `gameIndex?` |
| `s4mt_project_build` | `project`, `outputRoot`, `gameIndex?` |
| `s4mt_game_index` | `gameRoot`, `output` |
| `s4mt_game_query` | `index`, `tgi` |
| `s4mt_game_find` | `index`, `type`, `limit?` |
| `s4mt_tdesc_refresh` | `fromDir`, `gameVersion`, `source`, `output` |
| `s4mt_tdesc_inspect` | `snapshot` |
| `s4mt_stbl_build` | `json`, `output` |
| `s4mt_stbl_export` | `package`, `tgi`, `output` |
| `s4mt_stbl_import` | `package`, `json`, `output` |
| `s4mt_casp_inspect` | `package`, `tgi` |
| `s4mt_casp_rename` | `package`, `tgi`, `name`, `output` |
| `s4mt_tuning_extract` | `package`, `combinedTgi`, `name`, `output` |
| `s4mt_tuning_build` | `tgi`, `xml`, `output`, `snapshot?`, `gameVersion?`, `snapshotSha256?` |
| `s4mt_tuning_validate` | `xml`, `tgi?`, `snapshot?`, `gameVersion?`, `snapshotSha256?` |
| `s4mt_tuning_export` | `package`, `tgi`, `output` |
| `s4mt_tuning_import` | `package`, `tgi`, `xml`, `output`, `snapshot?`, `gameVersion?`, `snapshotSha256?` |
| `s4mt_simdata_inspect` | `package`, `tgi` |
| `s4mt_simdata_roundtrip` | `package`, `tgi`, `output` |
| `s4mt_simdata_set` | `package`, `tgi`, `table`, `row`, `column`, `value`, `output` |
| `s4mt_image_export` | `package`, `tgi`, `output?`, `bundle?`, `mip?` |
| `s4mt_image_build` | `tgi`, `png?`, `bundle?`, `output` |
| `s4mt_image_import` | `package`, `tgi`, `png?`, `bundle?`, `output` |
| `s4mt_conflict_game` | `package`, `index` |
| `s4mt_conflict_packages` | `left`, `right` |

The catalog in `src/S4MT.Cli/CommandCatalog.cs` defines option roles and report contracts. The CLI validates its invocation against this catalog, and the MCP host generates its tool schemas from it. `dotnet test tests/S4MT.Mcp.Tests` checks the inventory against the CLI's reports and runs all 32 tools through stdio with the real CLI and generated fixtures.

## Inspect a package

Call `s4mt_inspect` with an absolute server path.

```json
{"package":"C:/work/mod-authoring/example.package"}
```

The report contains `resourceCount` and `resources`. Each resource has `type`, `group`, `instance`, and `sizeBytes`. Use a resource's TGI with the typed inspect tools or `s4mt_resource_export`.

## Image alternatives and schema pins

`s4mt_image_export` requires exactly one of `output` and `bundle`. `output` writes one PNG. RLES requires `bundle`, which writes `color.png`, `mask.png`, and `manifest.json` into a new directory. Optional `mip` selects the exported mip level where the codec supports it.

`s4mt_image_build` and `s4mt_image_import` require exactly one of `png` and `bundle`. RLES requires a mip-0 bundle and produces a full mip chain. Supported codecs and pixel verification have the same limits as the CLI.

`s4mt_tuning_build`, `s4mt_tuning_validate`, and `s4mt_tuning_import` accept the optional `snapshot`, `gameVersion`, and `snapshotSha256` parameters together. `s4mt_simdata_set` takes a JSON integer `row` and a string `value`, including numeric values such as `"1.25"`. SimData scalar and string restrictions are the CLI's restrictions.

## Paths and output ownership

Direct path arguments must be absolute paths on the server. Read paths must resolve inside `readRoots` or `gameRoots`. Write paths must resolve inside `writeRoots`. Configured game roots and the active Mods directory remain read-only.

Standalone build and verify manifests may use absolute resource sources or paths relative to the manifest. Merge manifests may also use either form. Every source must resolve inside a configured read root or game root. Project paths retain the CLI's stricter containment rules. Game, TDesc, schema, and RLES trees are checked before execution, including linked descendants. The host limits checked trees to 20,000 entries and JSON path metadata to 1 MiB.

File outputs replace existing files only after a successful CLI report. The host stages each file in a private sibling directory and reserves its destination with a persistent `.s4mt-mcp-*.lock` file. Calls targeting the same destination return `output_busy` while a writer holds the reservation. Lock files stay in place between calls. Do not delete them while the host is running. Unknown files and unrelated staging directories are preserved.

Caller-selected output paths cannot contain components starting with `.s4mt-mcp` or `.s4mt-stage-`. These names are reserved for locks, ownership markers, and staging. The check includes resolved link targets.

Standalone file and bundle outputs cannot write inside a managed project build root. Use a destination outside that root.

RLES bundle exports require a new directory. TDesc refresh reuses an existing snapshot only when the CLI verifies identical data. Project builds retain their dedicated output root, ownership marker, child identity journal, and cleanup recovery rules. A `cleanup_failed` reservation blocks reuse and preserves staging data for inspection. Use a new output path until the earlier process cleanup has been checked. Abrupt host termination can leave private file staging directories, which are not automatically treated as owned by a later invocation.

Do not change input files, links, or output directories while an invocation is running. Root checks protect static local paths and are not an OS sandbox against hostile concurrent filesystem changes.

## Reports and limits

`structuredContent` contains the CLI report, including unknown nested fields. The host maps staged file output paths back to their requested destinations. `_meta.cliExitCode` and `_meta.cliStderr` retain process metadata. Cancellation, timeout, output limits, and host shutdown use the same contained process runner for every tool.

Exit 1 with `valid:false` remains a successful tool response for `verify`, `tuning.validate`, `project.validate`, and `project.build`. CLI errors without that report remain tool errors. Byte and codec verification does not prove in-game behavior.

The default stdout cap is 1 MiB. A large package inspection can exceed it. Set `maximumStdoutBytes` in the host configuration to a larger value, up to 16 MiB, and restart the host. The host returns `output_limit` and stops the child when capture exceeds the limit. `maximumStderrBytes` and `timeoutMilliseconds` are also configurable.

`s4mt_project_validate` and `s4mt_project_build` preserve the public `gameIndex` parameter. When omitted, they use the active server cache if configured. The selected cache's SHA-256, game version, and fingerprint are captured in `_meta`. Stdio can load an existing cache with `gameIndexCacheRoot`, which must be disjoint from the configured roots. Game query, find, and conflict tools use their explicit `index` argument.

After upgrading, rebuild and restart the MCP host. Reconnect the client or refresh its tool inventory to discover the 32 tools. See [Run the MCP tools over stdio](mcp-local-launch.md) and [Host the MCP tools on a Linux homelab](mcp-homelab.md) for transport setup.
