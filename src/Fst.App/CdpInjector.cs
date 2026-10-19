using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Fst.App;

public sealed class CdpInjector(JobService jobs)
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<string, ClientWebSocket> sockets = new();
    private int sequence;
    // Last logged error per source, so a failure repeating every poll is written to the log once.
    private string? lastPollingError;
    private readonly Dictionary<string, string> socketErrors = new();
    public int StoreTabs
    {
        get; private set;
    }
    public bool Connected
    {
        get; private set;
    }
    public async Task RunAsync(CancellationToken ct)
    {
        using var resource = typeof(CdpInjector).Assembly.GetManifestResourceStream("Fst.App.StoreScript.js") ?? throw new InvalidOperationException("StoreScript отсутствует.");
        using var reader = new StreamReader(resource);
        var script = await reader.ReadToEndAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var list = JsonDocument.Parse(await http.GetStringAsync("http://127.0.0.1:8080/json", ct));
                if (lastPollingError is not null)
                {
                    JobService.Log("Соединение с Steam CEF восстановлено.");
                    lastPollingError = null;
                }
                Connected = true;
                StoreTabs = 0;
                var live = new HashSet<string>();
                foreach (var tab in list.RootElement.EnumerateArray())
                {
                    if (!tab.TryGetProperty("url", out var u) || !Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri) || uri.Host != "store.steampowered.com")
                        continue;
                    if (!tab.TryGetProperty("webSocketDebuggerUrl", out var w))
                        continue;
                    var wsUrl = w.GetString()!;
                    if (!Uri.TryCreate(wsUrl, UriKind.Absolute, out var wsUri) || wsUri.Scheme != "ws" || !wsUri.IsLoopback)
                        continue;
                    var id = tab.GetProperty("id").GetString()!;
                    live.Add(id);
                    StoreTabs++;
                    try
                    {
                        if (!sockets.TryGetValue(id, out var socket) || socket.State != WebSocketState.Open)
                        {
                            socket?.Dispose();
                            socket = new();
                            await socket.ConnectAsync(wsUri, ct);
                            sockets[id] = socket;
                        }
                        await Evaluate(socket, script, ct);
                        var pending = await Evaluate(socket, "JSON.stringify((()=>{const b=window.__FST;if(!b)return {};const r=b.requests;b.requests={};return r})())", ct);
                        if (pending is null)
                            continue;
                        using var requests = JsonDocument.Parse(pending);
                        foreach (var req in requests.RootElement.EnumerateObject())
                        {
                            if (req.Name.Length > 100)
                                continue;
                            object reply;
                            try
                            {
                                reply = new
                                {
                                    result = jobs.Dispatch(req.Value.GetProperty("method").GetString()!, req.Value.GetProperty("args"))
                                };
                            }
                            catch (Exception e) { reply = new { error = L.T(e.Message) }; }
                            var json = JsonSerializer.Serialize(reply, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                            await Evaluate(socket, "window.__FST.responses[" + JsonSerializer.Serialize(req.Name) + "]=" + json, ct);
                        }
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        if (sockets.Remove(id, out var old))
                            old.Dispose();
                        var error = Describe(e);
                        if (!socketErrors.TryGetValue(id, out var previous) || previous != error)
                        {
                            JobService.Log("Ошибка WebSocket Steam CEF ({0}): {1}", id, error);
                            socketErrors[id] = error;
                        }
                    }
                }
                foreach (var id in sockets.Keys.Where(k => !live.Contains(k)).ToArray())
                {
                    sockets[id].Dispose();
                    sockets.Remove(id);
                }
                // Forget errors of tabs that were closed or left the store, so the map does not grow for the whole session.
                foreach (var id in socketErrors.Keys.Where(k => !live.Contains(k)).ToArray())
                    socketErrors.Remove(id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                Connected = false;
                StoreTabs = 0;
                var error = Describe(e);
                if (lastPollingError != error)
                {
                    JobService.Log("Ошибка чтения Steam CEF: {0}", error);
                    lastPollingError = error;
                }
            }
            await Task.Delay(400, ct);
        }
        foreach (var socket in sockets.Values)
            socket.Dispose();
        http.Dispose();
    }
    private static string Describe(Exception e) => e.GetType().Name + ": " + e.Message;
    private async Task<string?> Evaluate(ClientWebSocket socket, string expression, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var id = ++sequence;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id,
            method = "Runtime.evaluate",
            @params = new
            {
                expression,
                returnByValue = true
            }
        });
        await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[65536];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult chunk;
            do
            {
                chunk = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                if (chunk.MessageType == WebSocketMessageType.Close)
                    throw new IOException("CEF socket closed.");
                if (message.Length + chunk.Count > 2 * 1024 * 1024)
                    throw new IOException("CEF response too large.");
                message.Write(buffer, 0, chunk.Count);
            } while (!chunk.EndOfMessage);
            using var result = JsonDocument.Parse(message.ToArray());
            if (!result.RootElement.TryGetProperty("id", out var rid) || rid.GetInt32() != id)
                continue;
            if (result.RootElement.TryGetProperty("error", out var err))
                throw new IOException(err.ToString());
            var body = result.RootElement.GetProperty("result");
            if (body.TryGetProperty("exceptionDetails", out var exception))
                throw new IOException(exception.ToString());
            var value = body.GetProperty("result");
            return value.TryGetProperty("value", out var v) ? v.ToString() : null;
        }
    }
}
