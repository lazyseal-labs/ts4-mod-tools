using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ModelContextProtocol.Client;
using S4MT.Cli;
using Xunit;

namespace S4MT.Mcp.Tests;

public sealed class HttpParityTests
{
    [Fact]
    public async Task AuthenticatedHttpDiscoversAllToolsAndBuildsAndInspectsPackage()
    {
        using var workspace = new TestWorkspace(timeout: 20000);
        using var security = new TestWorkspace();
        using var cache = new TestWorkspace();
        var tokenFile = Path.Combine(security.Root, "mcp.token");
        var adminFile = Path.Combine(security.Root, "admin.token");
        const string testToken = "mcp-http-test-fixture-token-not-for-deployment";
        File.WriteAllText(tokenFile, testToken);
        File.WriteAllText(adminFile, "mcp-http-admin-fixture-token-not-for-deployment");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cli = Path.Combine(TestWorkspace.RepositoryRoot, "src", "S4MT.Cli", "bin", configuration, "net10.0", "s4mt.dll");
        workspace.SaveConfig(workspace.Options with
        {
            CliPath = cli, Transport = "http", HttpListenUrl = endpoint.GetLeftPart(UriPartial.Authority),
            HttpBearerTokenFile = tokenFile, CacheAdminBearerTokenFile = adminFile, GameIndexCacheRoot = cache.Root
        });
        var start = new ProcessStartInfo(workspace.Dotnet)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { workspace.Host, "--config", workspace.Configuration }) start.ArgumentList.Add(argument);
        using var host = Process.Start(start)!;
        var stdout = host.StandardOutput.ReadToEndAsync();
        var stderr = host.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var http = new HttpClient();
            while (true)
            {
                Assert.False(host.HasExited, "HTTP host exited during startup.");
                try
                {
                    using var response = await http.GetAsync(endpoint, deadline.Token);
                    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                    break;
                }
                catch (HttpRequestException) { await Task.Delay(50, deadline.Token); }
            }
            await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = endpoint, AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + testToken }
            }), cancellationToken: deadline.Token);
            Assert.Equal(CommandCatalog.All.Select(command => command.ToolName).Order(), (await client.ListToolsAsync(cancellationToken: deadline.Token)).Select(tool => tool.Name).Order());
            var json = Path.Combine(workspace.Root, "strings.json");
            var package = Path.Combine(workspace.Root, "strings.package");
            File.WriteAllText(json, JsonSerializer.Serialize(new { format = 1, tgi = "0x220557DA:0x00000000:0x0000000000000001", entries = new[] { new { key = "0x12345678", text = "Hello HTTP" } } }));
            var build = await client.CallToolAsync("s4mt_stbl_build", new Dictionary<string, object?> { ["json"] = json, ["output"] = package }, cancellationToken: deadline.Token);
            Assert.False(build.IsError == true, JsonSerializer.Serialize(build));
            var inspect = await client.CallToolAsync("s4mt_inspect", new Dictionary<string, object?> { ["package"] = package }, cancellationToken: deadline.Token);
            Assert.False(inspect.IsError == true, JsonSerializer.Serialize(inspect));
            Assert.Equal(1, inspect.StructuredContent!.Value.GetProperty("resourceCount").GetInt32());
        }
        finally
        {
            if (!host.HasExited) host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(stdout, stderr);
        }
    }
}
