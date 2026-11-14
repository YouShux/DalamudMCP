using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DalamudMCP.Server;
using DalamudMCP.Shared;
using ModelContextProtocol.Protocol;
using DalamudMCP.Plugin;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS " + name); checks++;
}
Check(LocalInstances.CandidatePorts(65535).Take(3).SequenceEqual([65535, 1024, 1025]), "port range wraps without privileged or invalid ports");
Check(LocalInstances.CandidatePorts(61016).Distinct().Count() == 64, "64 distinct bind candidates");
var firstTrace = new FakeNativeTrace(Path.Combine(Path.GetTempPath(), "trace.jsonl"));
var secondTrace = new FakeNativeTrace(firstTrace.FilePath);
NativeTraceLog.Isolate(firstTrace, 101);
NativeTraceLog.Isolate(secondTrace, 202);
Check(firstTrace.FilePath != secondTrace.FilePath && firstTrace.FilePath.EndsWith("trace-101.jsonl"),
    "native audit paths are isolated by game process even with readonly runtime properties");
var selectedTracePath = firstTrace.FilePath;
NativeTraceLog.Isolate(firstTrace, 101);
Check(firstTrace.FilePath == selectedTracePath, "native audit isolation is idempotent");
var customTrace = new FakeNativeTrace(Path.Combine(Path.GetTempPath(), "custom.jsonl"));
var customTracePath = customTrace.FilePath;
NativeTraceLog.Isolate(customTrace, 101);
Check(customTrace.FilePath == customTracePath, "explicit native audit paths are preserved");
Check(NativeBridge.ReadProcessID(new() { Content = [new TextContentBlock { Text = "{\"processId\":42}" }] }) == 42,
    "native process identity parses actual control state");
Check(NativeBridge.ReadProcessID(new() { IsError = true, Content = [new TextContentBlock { Text = "{\"processId\":42}" }] }) is null,
    "native errors cannot authorize a connection");
Check(NativeBridge.ReadProcessID(new() { Content = [new TextContentBlock { Text = "{}" }] }) is null,
    "missing process identity is rejected");
Check(NativeBridge.ReadProcessID(new() { Content = [new TextContentBlock { Text = "{\"processId\":\"42\"}" }] }) is null,
    "malformed process identity is rejected");
Check(NativeBridge.ReadProcessID(new() { StructuredContent = ToolJson.Element(new { status = "completed", sharedRuntime = true, result = new { processId = 42 } }) }) == 42,
    "installed native runtime completion envelope is supported");
Check(NativeBridge.ReadProcessID(new() { StructuredContent = ToolJson.Element(new { status = "pending", result = new { processId = 42 } }) }) is null,
    "incomplete native operations cannot authorize a connection");

var nativePID = Environment.ProcessId;
var nativeBuilder = WebApplication.CreateSlimBuilder();
nativeBuilder.Logging.ClearProviders();
nativeBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
await using var nativeApp = nativeBuilder.Build();
nativeApp.MapPost("/mcp/", async (HttpContext context) =>
{
    using var document = await JsonDocument.ParseAsync(context.Request.Body);
    var root = document.RootElement;
    if (!root.TryGetProperty("id", out var id)) { context.Response.StatusCode = 202; return; }
    object result = root.GetProperty("method").GetString() switch
    {
        "initialize" => new { protocolVersion = "2025-06-18", capabilities = new { tools = new { } }, serverInfo = new { name = "dalamud-mcp", version = "2.0.0" } },
        "tools/list" => new { tools = new[] { new { name = "control", inputSchema = new { type = "object" } } } },
        "tools/call" => new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(new { status = "completed", sharedRuntime = true, result = new { processId = nativePID } }) } } },
        _ => new { },
    };
    await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id = id.Clone(), result });
});
await nativeApp.StartAsync();
var nativePort = new Uri(nativeApp.Urls.Single()).Port;
await using (var bridge = new NativeBridge())
{
    await bridge.ConnectAsync(nativePort, Environment.ProcessId + 1, false, default);
    Check(bridge.Current is null && bridge.Error?.Contains("PID") == true, "another game's native endpoint is refused");
    await bridge.ConnectAsync(nativePort, Environment.ProcessId, true, default);
    Check(bridge.Current?.GameProcessID == Environment.ProcessId, "matching native process connects");
    nativePID++;
    try { await NativeBridge.VerifyProcessAsync(bridge.Current!.Client, Environment.ProcessId, default); throw new Exception("identity change accepted"); }
    catch (InvalidOperationException) { Check(true, "identity is rechecked before forwarding an actual call"); }
    nativePID--;
}

if (args.Length == 0) throw new ArgumentException("Pass the built gateway DLL path for concurrent-process tests.");
var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
using var blocked = new TcpListener(IPAddress.Loopback, 0);
blocked.Start();
var preferred = ((IPEndPoint)blocked.LocalEndpoint).Port;
await using var firstPipe = new FakeGame(nativePort);
await using var secondPipe = new FakeGame(nativePort);
await using var thirdPipe = new FakeGame(nativePort);
Process? first = null, second = null, third = null;
try
{
    // Start concurrently; no probe/reserve/release gap exists in gateway binding.
    first = Start(args[0], firstPipe.Name, preferred, token);
    second = Start(args[0], secondPipe.Name, preferred, token);
    third = Start(args[0], thirdPipe.Name, preferred, token);
    var ports = await Task.WhenAll(Ready(first), Ready(second), Ready(third));
    Check(ports.Distinct().Count() == 3 && ports.All(port => port != preferred), "three concurrent gateways avoid occupied and each other's ports");
    using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
    Check((await http.GetAsync($"http://127.0.0.1:{ports[0]}/status")).StatusCode == HttpStatusCode.Unauthorized,
        "fallback endpoints retain bearer authentication");
    http.DefaultRequestHeaders.Authorization = new("Bearer", token);
    for (var index = 0; index < 3; index++)
    {
        var status = await http.GetFromJsonAsync<GatewayStatus>($"http://127.0.0.1:{ports[index]}/status", ToolJson.Options);
        Check(status?.GameProcessID == Environment.ProcessId && status.InstanceID == new[] { firstPipe.ID, secondPipe.ID, thirdPipe.ID }[index],
            "gateway status remains attached to its own game pipe " + index);
    }
    Check(LocalInstances.List().Count(entry => new[] { firstPipe.ID, secondPipe.ID, thirdPipe.ID }.Contains(entry.InstanceID)) == 3,
        "instance discovery retains all three independent endpoints");
    first.Kill(entireProcessTree: true); await first.WaitForExitAsync();
    Check(LocalInstances.List().All(entry => entry.InstanceID != firstPipe.ID), "dead gateway records are ignored");
    var surviving = await http.GetFromJsonAsync<GatewayStatus>($"http://127.0.0.1:{ports[1]}/status", ToolJson.Options);
    Check(surviving?.InstanceID == secondPipe.ID, "stopping the first instance leaves the second usable");
}
finally
{
    foreach (var process in new[] { first, second, third })
    {
        if (process is null) continue;
        if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        process.Dispose();
    }
    foreach (var id in new[] { firstPipe.ID, secondPipe.ID, thirdPipe.ID }) File.Delete(Path.Combine(LocalInstances.DirectoryPath, id + ".json"));
}
await nativeApp.StopAsync();
Console.WriteLine($"{checks} checks passed.");

static Process Start(string dll, string pipe, int port, string token)
{
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var value in new[] { dll, "--pipe", pipe, "--port", port.ToString(), "--parent", Environment.ProcessId.ToString() }) start.ArgumentList.Add(value);
    start.Environment["DALAMUD_MCP_BEARER_TOKEN"] = token;
    var process = Process.Start(start)!;
    process.ErrorDataReceived += (_, _) => { };
    process.BeginErrorReadLine();
    return process;
}

static async Task<int> Ready(Process process)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
        if (line.Split(':') is ["DALAMUDMCP_READY", var port, _]) return int.Parse(port);
    throw new Exception("Gateway exited without reporting ready: " + process.ExitCode);
}

sealed class FakeGame : IAsyncDisposable
{
    public string ID { get; } = Guid.NewGuid().ToString("N");
    public string Name { get; } = "DalamudMCP.Tests." + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task listener;
    private readonly int nativePort;
    public FakeGame(int nativePort) { this.nativePort = nativePort; listener = Listen(); }
    private async Task Listen()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(lifetime.Token); }
                catch { pipe.Dispose(); throw; }
                _ = Handle(pipe);
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task Handle(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true))
        await using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true })
        {
            var request = JsonSerializer.Deserialize<PipeRequest>((await reader.ReadLineAsync())!, ToolJson.Options)!;
            var settings = new BridgeSettings(nativePort, false, false, [], Environment.ProcessId, ID);
            var value = request.Method == "settings" ? ToolJson.Element(settings) : ToolJson.Element(new ToolCatalog([], settings));
            await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeResponse(value), ToolJson.Options));
        }
    }
    public async ValueTask DisposeAsync() { lifetime.Cancel(); await listener; lifetime.Dispose(); }
}

sealed class FakeNativeTrace(string filePath)
{
    public string FilePath { get; } = filePath;
}
