# S4MT

S4MT is a JSON-first command-line toolkit for Sims 4 mod projects. It builds DBPF packages, handles selected resource formats, indexes installed game packages, and reports resource conflicts. It does not install builds into the game.

The runtime for `.ts4script` stays Python. S4MT validates script archives and copies them byte-for-byte into build output.

## Requirements

- .NET 10 SDK
- LlamaLogic.Packages 3.8.2
- SixLabors.ImageSharp 3.1.11

Run the CLI with:

```powershell
dotnet run --project src/S4MT.Cli -- --help
```

Commands return one JSON object on stdout. Errors return JSON on stderr. Responses have `schemaVersion`; exit codes are `0` for success, `2` for invalid input, `1` for an operation failure, and `130` for cancellation.

## Package and project workflow

```powershell
dotnet run --project src/S4MT.Cli -- project init --name CozyCareers --path .\CozyCareers
dotnet run --project src/S4MT.Cli -- project validate --project .\CozyCareers
dotnet run --project src/S4MT.Cli -- project build --project .\CozyCareers --output-root .\dist
```

The version 1 package manifest declares each full TGI key and source path. Paths are relative to the manifest directory unless absolute:

```json
{
  "format": 1,
  "resources": [
    {
      "type": "0x03B33DDF",
      "group": "0x00000000",
      "instance": "0x0123456789ABCDEF",
      "source": "resources/my_tuning.xml",
      "encoding": "utf8"
    }
  ]
}
```

The generic `build` command copies UTF-8 or raw bytes under explicit TGI keys. It writes to a temporary package, reopens the result, and compares the exact keys and payloads before replacing the destination. Project builds use content-fingerprinted output directories. Repeating the same build reuses the verified output.

Project format 2 can pin a local schema snapshot. Keep the snapshot directory inside the project (for example under `.s4mt/tdesc`) and add this property to `s4mt.json`:

```json
{
  "tuningSchema": {
    "snapshot": ".s4mt/tdesc",
    "gameVersion": "1.128.90.1030",
    "sha256": "<digest printed by tdesc refresh>"
  }
}
```

The repeatable reference brief verifier is `pwsh -NoProfile -File scripts/verify-agent-mod-authoring.ps1`. It starts from the installed Polish string table, edits one entry, validates and builds a clean project, reopens the output package, and checks the expected game conflict. Its report deliberately separates that structural result from runtime behavior.

To create a persistent, manually testable example mod, run `pwsh -NoProfile -File scripts/create-polish-menu-mod.ps1`. It writes the source project to `examples/PolishMenuLabel` and the ready-to-copy package to `dist/S4MT-PolishMenuLabel.package`. Follow [the example's manual test steps](examples/PolishMenuLabel/README.md); the mod changes the Polish main-menu label `Nowa gra` to `Nowa gra (S4MT test)`.

To create a gameplay tuning example, run `pwsh -NoProfile -File scripts/create-stressed-mood-color-mod.ps1`. It extracts the current `Mood_Stressed` Instance tuning from the installed binary CombinedTuning resource, changes SteelSeries colors and selects the existing green `Inspired`/`Imaginative` portrait frame styles while preserving the mood-name strings, validates against a pinned Mood schema, and builds `dist/S4MT-StressedMood-Green.package`.

`project validate` parses recognized tuning resources declared as `utf8` and reports malformed XML against the source file. For XML rooted at `combined`, it also checks that direct `<g>` definitions have unique `x` values and that every `<r x>` refers to one definition. Tuning declared as raw `bytes` remains opaque. STBL, SimData, PNGImage, DST, LRLE, and recognized TS4 translucent JPEG resources must use `bytes`; a `utf8` declaration returns an error with the resource source path. For `bytes` resources, validation decodes STBL entries and supported images, including every declared LRLE mip, and attempts a LlamaLogic decode/encode/read-back for `SimData`. Image errors include the TGI and source path. If LlamaLogic cannot validate a SimData resource, validation emits a warning; the resource stays available as opaque bytes. Resources without a project validator also remain opaque.

Project format 3 can declare exact resource relationships in `s4mt.json`. `requirement: "observed"` records a relationship without making the target part of this package. `requirement: "bundled"` is an author assertion that the exact target TGI must be in the package; a missing bundled target blocks validation/build. Both forms require the source TGI to be in the package, and bundled assertions require a reason:

```json
{
  "format": 3,
  "name": "CozyCareers",
  "packageManifest": "package.json",
  "scriptArchives": [],
  "dependencies": [
    {
      "source": "0x03B33DDF:0x00000000:0x0123456789ABCDEF",
      "target": "0x39B2AA4A:0x00000000:0xFEDCBA9876543210",
      "requirement": "observed",
      "reason": "Game-provided audio resource"
    }
  ]
}
```

With a pinned TDESC snapshot, project reports also include full-TGI `TunableResourceKey` observations plus logical-only tuning/string values. `--game-index` adds indexed provider evidence: `project validate --project .\\CozyCareers --game-index <index>` and `project build --project .\\CozyCareers --output-root .\\dist --game-index <index>`. Missing keys and types are advisory because the catalog can omit valid resource types. Reports retain all providers and never claim an effective load-order winner. Reference scanning is partial and currently covers supported top-level TDESC values and nested lists/tuples; other resources are explicitly marked not scanned. Validation and build use the same prepared package/script bytes.

File-writing commands reject output paths that resolve to an input, including paths reached through symbolic links or junctions. The game index output must resolve outside the scanned game directory, and project resource/script paths are checked against their resolved project root.

## Game catalog and conflicts

```powershell
dotnet run --project src/S4MT.Cli -- game index --game-root 'C:\Program Files\EA Games\The Sims 4' --output "$env:LOCALAPPDATA\S4MT\cache\game-index.jsonl.gz"
dotnet run --project src/S4MT.Cli -- game query --index "$env:LOCALAPPDATA\S4MT\cache\game-index.jsonl.gz" --tgi 0x220557DA:0x00000000:0x0034C6AB4A0612D5
dotnet run --project src/S4MT.Cli -- game find --index "$env:LOCALAPPDATA\S4MT\cache\game-index.jsonl.gz" --type 0x545AC67A --limit 10
dotnet run --project src/S4MT.Cli -- conflict game --package .\dist\mod.package --index "$env:LOCALAPPDATA\S4MT\cache\game-index.jsonl.gz"
dotnet run --project src/S4MT.Cli -- conflict packages --left .\dist\mod.package --right .\other-mod.package
```

The index reads package keys only and keeps every provider for a TGI. The cache is outside the game directory. Delta package files are surfaced as patch-tier hints, but S4MT does not claim an effective winner until game load precedence is verified against runtime behavior. The index command scans the supplied game root only; it does not scan or modify the user's Mods folder.

Run `pwsh -NoProfile -File scripts/verify-agent-mod-authoring.ps1` for a clean-project example: change one Polish main-menu STBL entry, preserve all other entries, validate/build the one-resource package, and report its game TGI conflict. The script cleans its temporary authoring files; it does not install or launch the mod.

## Resource commands

STBL JSON preserves the full resource TGI, the locale encoded in the high byte of `instance`, and each 32-bit string key. Build and import reject a declared locale that does not match the TGI. Unsupported locale prefixes report `null`:

```powershell
dotnet run --project src/S4MT.Cli -- stbl export --package .\source.package --tgi 0x220557DA:0x00000000:0x0123456789ABCDEF --output .\strings.json
dotnet run --project src/S4MT.Cli -- stbl build --json .\strings.json --output .\strings.package
dotnet run --project src/S4MT.Cli -- stbl import --package .\source.package --json .\strings.json --output .\updated.package
```

CASP v52 inspection and fixed-length name renaming preserve every byte outside the name field. Only printable ASCII replacements with the same encoded byte length are accepted; other versions, fields, and variable-length names remain unsupported:

```powershell
dotnet run --project src/S4MT.Cli -- casp inspect --package .\source.package --tgi 0x034AEECB:0x00000000:0x000000000007D39A
dotnet run --project src/S4MT.Cli -- casp rename --package .\source.package --tgi 0x034AEECB:0x00000000:0x000000000007D39A --name ymShoes_EV007Boots_SolidBlue --output .\updated.package
```

Run `pwsh -NoProfile -File scripts/verify-casp-cli.ps1` to repeat the v52 game-fixture and package read-back check.

Tuning XML commands support well-formed XML resources whose TGI type is a recognized tuning type:

```powershell
dotnet run --project src/S4MT.Cli -- tuning extract --package 'C:\Program Files\EA Games\The Sims 4\Data\Simulation\SimulationDeltaBuild0.package' --combined-tgi 0x62E94D38:0x00000000:0xF1352E740C4710BA --name Mood_Stressed --output .\Mood_Stressed.xml
dotnet run --project src/S4MT.Cli -- tuning export --package .\source.package --tgi 0x62E94D38:0x00000000:0x0123456789ABCDEF --output .\tuning.xml
dotnet run --project src/S4MT.Cli -- tuning validate --xml .\tuning.xml --tgi 0x62E94D38:0x00000000:0x0123456789ABCDEF
dotnet run --project src/S4MT.Cli -- tuning build --tgi 0x62E94D38:0x00000000:0x0123456789ABCDEF --xml .\tuning.xml --output .\tuning.package
```

`tuning extract` decodes binary CombinedTuning and returns one named Instance tuning with its shared-table references expanded, plus the standalone tuning TGI calculated from the root name. It uses LlamaLogic's eager CombinedTuning model; one installed game resource reached a 1.285 GB working set, so extraction is opt-in and project validation never runs it automatically. The game index still indexes package-level TGIs only, so `conflict game` does not yet report individual tuning entries embedded in CombinedTuning and does not establish effective package precedence. `tuning validate` checks XML well-formedness, reports root metadata, and calculates FNV32/FNV64 name-hash candidates (including the high-bit variants). For CombinedTuning XML, it also validates the shared `<g>/<r x>` indexes. Supplying all three of `--snapshot`, `--game-version`, and `--snapshot-sha256` adds pinned `.tdesc` field validation; schema errors also block standalone tuning builds/imports and project builds. Coverage is deliberately partial: unsupported nodes, enum values, tuning references, absent required fields, and target existence are reported or documented as unverified. Binary CombinedTuning remains opaque in project validation. `simdata inspect` reports table schemas, and `simdata roundtrip` verifies LlamaLogic's model encode/decode path. `simdata set` edits one existing BOOL/integer/FLOAT, ASCII `STRING8`, or fixed `FLOAT3`/`FLOAT4` cell in a uniquely named table and column. String edits require a decodable existing reference and reject embedded NUL and non-ASCII input. Fixed vectors use comma-separated finite numbers and require the exact component count. `FLOAT2`, `HASHEDSTRING8`, other references, dynamic `VECTOR`, and table-shape edits remain unsupported. It preserves unrelated package resources and reads the changed cell back before replacing output:

```powershell
dotnet run --project src/S4MT.Cli -- simdata set --package .\source.package --tgi 0x545AC67A:0x0003C397:0xE7D7D14BBEA510D9 --table BuildBuy --row 0 --column BlockAreaLarge --value 1.25 --output .\updated.package
dotnet run --project src/S4MT.Cli -- simdata set --package .\source.package --tgi 0x545AC67A:0x0003C397:0xE7D7D14BBEA510D9 --table BuildBuy --row 0 --column MagalogHighlight --value 0.1,0.2,0.3 --output .\updated-vector.package
```

TDesc schemas can be imported into a local JSON snapshot and inspected offline:

```powershell
dotnet run --project src/S4MT.Cli -- tdesc refresh --from-dir .\tdesc-source --game-version 1.128.90.1030 --source "locally obtained EA schema pack" --output .\schema-cache\tdesc-1.128.90.1030
dotnet run --project src/S4MT.Cli -- tdesc inspect --snapshot .\schema-cache\tdesc-1.128.90.1030
```

The importer records the supplied game version and source label, hashes every `.tdesc` file, and stores a compact index plus hash-addressed declaration shards without copying raw schemas. It rejects DTDs, unsupported root definitions, duplicate class identities, oversized input, and changed content at an occupied snapshot path. The version and source label are declarations from the caller; S4MT does not prove that the files came from that game build. Use the exact snapshot digest printed by `tdesc refresh` to pin schema-aware tuning validation. Project format 2 opts in with a `tuningSchema` object containing a project-relative snapshot directory, game version, and digest; format 3 supports the same pin plus dependency assertions; project format 1 stays unchanged. Run `pwsh -NoProfile -File scripts/verify-tdesc-snapshot.ps1` and `pwsh -NoProfile -File scripts/verify-tdesc-semantic-validation.ps1` for local checks.

For standalone checks, pass those three values to `tuning validate`, `tuning build`, or `tuning import`:

```powershell
dotnet run --project src/S4MT.Cli -- tuning validate --xml .\tuning.xml `
  --snapshot "$env:LOCALAPPDATA\S4MT\cache\tdesc\1.128.90.1030" `
  --game-version 1.128.90.1030 --snapshot-sha256 <digest>
```

Image commands write `DSTImage` (`0x00B2D882`), `PNGImage` (`0x2F7D0004`), `LRLEImage` (`0x2BC04EDF`), `RLE2Image` (`0x3453CF95`), RLES (`0xBA856C78`) and resource types LlamaLogic marks as TS4 translucent JPEG. DST and translucent JPEG conversions report pixel deltas and are not lossless. `PNGImage` stores raw PNG payloads and is verified byte-for-byte. LRLE v1/v2 export and v2 build/import passed package and exact RGBA read-back on installed game fixtures; in-game display remains unverified. RLE2 exports selected mips and builds/imports DXT5 resources with a generated full mip chain. Three pinned game fixtures were compared byte-for-byte in RGBA against FFmpeg's independent DDS decoder across 14 exports. A generated 32×16 gradient and 1×1 transparent and opaque inputs passed writer checks across all six mips and both alpha edge cases. The RLE2 writer is lossy; in-game display remains unverified. RLES exports selected mip pairs as a bundle containing `color.png`, `mask.png`, and `manifest.json`; three pinned fixtures and 13 exports matched independent color and mask pixel oracles. RLES build/import generates a full mip chain; one pinned 512×1024 fixture passed independent FFmpeg color and exact mask comparisons across all 11 mips, and import preserved an unrelated package resource. BC3 color encoding is lossy; in-game display remains unverified. `PNGImage2` and other resource variants still need fixture-backed codecs. Run `pwsh -NoProfile -File scripts/verify-rle2-export.ps1`, `pwsh -NoProfile -File scripts/verify-rle2-writer.ps1`, `pwsh -NoProfile -File scripts/verify-rles-export.ps1`, and `pwsh -NoProfile -File scripts/verify-rles-writer.ps1` to repeat the image checks. FFmpeg is needed for verification only.

RLE2 export accepts `--mip <index>` to select a mip level; the default is level 0.

RLES export, build, and import require `--bundle <directory>` so the color and mask stay paired. Export writes `color.png`, `mask.png`, and `manifest.json`; `--mip <index>` selects the mip for both PNGs. Build/import require a mip-0 bundle and create a full mip chain.

Use `package merge` to combine typed build outputs into a package. Inputs are listed in order in a format 1 JSON manifest. Duplicate TGIs fail by default; `onConflict` may be set to `first` or `last` to select explicitly. The merge verifies the final key set and every selected payload:

```json
{
  "format": 1,
  "packages": ["tuning.package", "strings.package", "images.package"],
  "onConflict": "error"
}
```

```powershell
dotnet run --project src/S4MT.Cli -- package merge --manifest .\merge.json --output .\mod.package
```

`resource export` and `resource import` provide a byte-level escape hatch for resource families without a typed codec. They preserve the exact decompressed payload and unrelated resources, but do not validate the internal format:

```powershell
dotnet run --project src/S4MT.Cli -- resource export --package .\source.package --tgi <type:group:instance> --output .\resource.bin
dotnet run --project src/S4MT.Cli -- resource import --package .\source.package --tgi <type:group:instance> --payload .\edited.bin --output .\updated.package
```

See [docs/resource-codecs.md](docs/resource-codecs.md) for the verified capability matrix and [PLAN.md](PLAN.md) for open work.

## Verification scripts

Run the package build/merge, path-collision, project, game-index, STBL, tuning, SimData, DST/PNGImage/translucent-JPEG image, raw-resource, and conflict workflows with the scripts under `scripts/`. `pwsh -NoProfile -File scripts/verify-tuning-shared-index.ps1` checks a real CombinedTuning XML fixture, malformed indexes, direct build/import rejection, project diagnostics, and opaque binary handling. The resource-specific scripts use fixtures from the installed Sims 4 version and keep generated files under the system temporary directory.

## MCP access

The MCP host exposes all 32 operational CLI commands over stdio or HTTP. Use `s4mt_inspect` to list resources in a `.package` file. Typed tools also cover resource editing, project builds, game indexes, TDesc snapshots, string tables, CASP, tuning, SimData, images, and conflicts.

See the [full MCP tool inventory and parameters](docs/mcp-tools.md), [local stdio setup](docs/mcp-local-launch.md), and [Linux HTTP setup](docs/mcp-homelab.md). Rebuild and restart an existing host, then refresh the client inventory to discover the added tools.
