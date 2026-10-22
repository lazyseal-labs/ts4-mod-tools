using System.Text.Json;
using S4MT.Cli;

namespace S4MT.Mcp
{
    internal sealed class PathPolicy(HostOptions options)
    {
        private const int MaximumFiles = 20_000;
        private readonly string? cacheRoot = ValidateCacheRoot(options);

        internal string Read(string path)
        {
            var full = HostOptions.Absolute(path);
            RejectDanglingLinks(full);
            if (!options.ReadRoots.Concat(options.GameRoots).Any(root => Within(full, root)))
                throw new McpFailure("path_outside_root", $"Read path is outside configured roots: {full}");
            return full;
        }

        internal string GameIndex(string path)
        {
            var full = HostOptions.Absolute(path);
            RejectDanglingLinks(full);
            if (cacheRoot is null || !Within(full, cacheRoot) || !File.Exists(full))
                throw new McpFailure("path_outside_root", "The active game index is outside the configured cache root or is missing.");
            return full;
        }

        internal string Write(string path)
        {
            var full = HostOptions.Absolute(path);
            RejectDanglingLinks(full);
            var mods = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Electronic Arts", "The Sims 4", "Mods");
            if (Within(full, mods) || options.GameRoots.Any(root => Within(full, root)))
                throw new McpFailure("read_only_root", "Game roots and the active Mods directory are read-only.");
            if (!options.WriteRoots.Any(root => Within(full, root)))
                throw new McpFailure("path_outside_root", $"Write path is outside configured roots: {full}");
            return full;
        }

        internal string Output(string path)
        {
            var full = Write(path);
            foreach (var candidate in new[] { full, PathGuard.ResolvePath(full) })
                foreach (var component in candidate.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
                    if (component.StartsWith(".s4mt-mcp", StringComparison.OrdinalIgnoreCase) ||
                        component.StartsWith(".s4mt-stage-", StringComparison.OrdinalIgnoreCase))
                        throw new McpFailure("reserved_output_path", "Tool outputs cannot target MCP locks, ownership markers, or staging paths.");
            return full;
        }

        internal string StandaloneOutput(string path)
        {
            var full = Output(path);
            for (var directory = PathGuard.ResolvePath(full); directory is not null; directory = Path.GetDirectoryName(directory))
                if (File.Exists(Path.Combine(directory, ".s4mt-mcp.lock")) || File.Exists(Path.Combine(directory, ".s4mt-mcp-owner.json")))
                    throw new McpFailure("output_in_project_build", "Standalone outputs cannot write inside a managed project build root.");
            return full;
        }

        internal IReadOnlyList<string> ProjectInputs(string project, bool initialize = false)
        {
            var inputs = new List<string>();
            var projectFile = Directory.Exists(project) || initialize ? Path.Combine(project, "s4mt.json") : project;
            var root = Path.GetDirectoryName(projectFile)!;
            Check(projectFile, root, initialize);
            inputs.Add(projectFile);
            if (initialize)
            {
                Check(Path.Combine(root, "package.json"), root, write: true);
                Check(Path.Combine(root, "resources"), root, write: true);
                Check(Path.Combine(root, "scripts"), root, write: true);
            }
            var document = ReadObject(projectFile);
            if (document is null) return inputs;
            if (String(document.Value, "packageManifest") is { } manifest)
            {
                var manifestPath = Relative(root, manifest, initialize);
                inputs.Add(manifestPath);
                var package = ReadObject(manifestPath);
                if (package is { } value && Property(value, "resources") is { ValueKind: JsonValueKind.Array } resources)
                {
                    foreach (var resource in resources.EnumerateArray())
                    {
                        if (String(resource, "source") is { } source)
                            inputs.Add(Relative(Path.GetDirectoryName(manifestPath)!, source, write: false));
                        if (inputs.Count > MaximumFiles) throw new McpFailure("input_limit", "Project has too many sources.");
                    }
                }
            }
            if (Property(document.Value, "scriptArchives") is { ValueKind: JsonValueKind.Array } scripts)
                foreach (var script in scripts.EnumerateArray())
                    if (script.ValueKind == JsonValueKind.String) inputs.Add(Relative(root, script.GetString()!, write: false));
            if (Property(document.Value, "tuningSchema") is { } schema && String(schema, "snapshot") is { } snapshot)
            {
                var snapshotPath = Relative(root, snapshot, write: false);
                CheckTree(snapshotPath, write: false, inputs);
            }
            return inputs;
        }

        internal IReadOnlyList<string> CommandInputs(string path, OptionRole role)
        {
            var inputs = new List<string> { Read(path) };
            if (role == OptionRole.Project) return ProjectInputs(path);
            if (role is OptionRole.Tree or OptionRole.Bundle)
            {
                CheckTree(path, write: false, inputs);
                return inputs;
            }
            if (role is not (OptionRole.Manifest or OptionRole.MergeManifest)) return inputs;
            var document = ReadObject(path);
            if (document is null) return inputs;
            var root = Path.GetDirectoryName(path)!;
            if (role == OptionRole.Manifest && Property(document.Value, "resources") is { ValueKind: JsonValueKind.Array } resources)
                foreach (var resource in resources.EnumerateArray())
                    if (String(resource, "source") is { } source) Add(source);
            if (role == OptionRole.MergeManifest && Property(document.Value, "packages") is { ValueKind: JsonValueKind.Array } packages)
                foreach (var package in packages.EnumerateArray())
                    if (package.ValueKind == JsonValueKind.String) Add(package.GetString()!);
            return inputs;

            void Add(string declaration)
            {
                var full = Path.IsPathFullyQualified(declaration) ? Path.GetFullPath(declaration) : Path.GetFullPath(Path.Combine(root, declaration));
                inputs.Add(Read(full));
                if (inputs.Count > MaximumFiles) throw new McpFailure("input_limit", "Manifest has too many sources.");
            }
        }

        internal void CheckTree(string root, bool write, List<string>? files = null)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            var count = 0;
            while (pending.TryPop(out var path))
            {
                if (write && (Directory.Exists(path) || File.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new McpFailure("unsafe_project_path", "Build output trees cannot contain linked files or directories.");
                if (write) Write(path);
                else Check(path, root, write: false);
                if (++count > MaximumFiles) throw new McpFailure("input_limit", "Directory exceeds the checked file limit.");
                files?.Add(path);
                if (Directory.Exists(path))
                    foreach (var entry in Directory.EnumerateFileSystemEntries(path)) pending.Push(entry);
            }
        }

        internal void NoCollision(string outputRoot, IEnumerable<string> inputs)
        {
            foreach (var input in inputs.Append(options.CliPath))
            {
                try { PathGuard.EnsureDoesNotOverwrite(outputRoot, (input, "input")); }
                catch (CliInputException exception) { throw new McpFailure("path_collision", exception.Message); }
                if (Within(input, outputRoot) || Directory.Exists(input) && Within(outputRoot, input))
                    throw new McpFailure("path_collision", "Output must not contain an input or the CLI, or be inside an input tree.");
            }
        }

        private string Relative(string root, string path, bool write)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
                throw new McpFailure("unsafe_project_path", "Indirect project paths must be relative.");
            var full = Path.GetFullPath(Path.Combine(root, path));
            Check(full, root, write);
            return full;
        }

        private void Check(string path, string root, bool write)
        {
            PathGuard.EnsureWithinDirectory(path, root, "Project path");
            if (write) Output(path);
            if (!write || File.Exists(path) || Directory.Exists(path)) Read(path);
        }

        private static JsonElement? ReadObject(string path)
        {
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 1_048_576)
                throw new McpFailure("input_limit", "Project metadata exceeds 1 MiB.");
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
            }
            catch (JsonException) { return null; }
        }

        private static string? String(JsonElement value, string name) =>
            Property(value, name) is { ValueKind: JsonValueKind.String } property ? property.GetString() : null;

        private static JsonElement? Property(JsonElement value, string name)
        {
            JsonElement? result = null;
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject())
                    if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) result = property.Value;
            return result;
        }

        internal static bool Within(string path, string root)
        {
            try { PathGuard.EnsureWithinDirectory(path, root, "Path"); return true; }
            catch (CliInputException) { return false; }
        }

        private static string? ValidateCacheRoot(HostOptions options)
        {
            if (options.GameIndexCacheRoot is null) return null;
            var cache = HostOptions.Absolute(options.GameIndexCacheRoot);
            if (options.ReadRoots.Concat(options.WriteRoots).Concat(options.GameRoots)
                .Any(root => Within(cache, root) || Within(root, cache)))
                throw new ArgumentException("gameIndexCacheRoot must be disjoint from readRoots, writeRoots, and gameRoots.");
            return cache;
        }

        private static void RejectDanglingLinks(string fullPath)
        {
            var current = Path.GetPathRoot(fullPath)!;
            foreach (var part in fullPath[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                FileAttributes attributes;
                try
                {
                    if (new FileInfo(current).LinkTarget is not null && !File.Exists(current) && !Directory.Exists(current))
                        throw new McpFailure("unsafe_project_path", $"Dangling links are not allowed in tool paths: {current}");
                    attributes = File.GetAttributes(current);
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0 && !File.Exists(current) && !Directory.Exists(current))
                    throw new McpFailure("unsafe_project_path", $"Dangling links are not allowed in tool paths: {current}");
            }
        }
    }
}

namespace S4MT.Cli
{
    internal sealed class CliInputException(string message) : Exception(message);
}
