using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ultron.Services;

/// <summary>LAN-only read-only web deck showing the live conversation and voice
    /// status, reachable from a QR code. Minimal HTTP/1.1 server built on
    /// TcpListener so no URL ACL reservation or admin rights are required.</summary>
public sealed class DashboardServer : IDisposable
{
    private const int MaxLogEntries = 200;
    private const string SseEventText = "\n";
    private readonly int _port;
    private readonly object _logLock = new();
    private readonly List<LogEntry> _log = new(MaxLogEntries);
    private readonly ConcurrentDictionary<long, SseClient> _sseClients = new();
    private long _clientSeq;

    public string Token { get; } = NewToken();
    public int ActualPort { get; private set; }
    public bool Running { get; set; }

    private string _status = "offline";
    private bool _awake;
    private bool _muted;
    private float _confidence;
    private string? _lastError;

    private sealed class LogEntry
    {
        public string T { get; init; } = "";
        public string Role { get; init; } = "";
        public string Text { get; init; } = "";
    }

    private sealed class SseClient
    {
        public TcpClient? Tcp;
        public NetworkStream? Stream;
        public ConcurrentQueue<string> Queue { get; } = new();
        public SemaphoreSlim Signal { get; } = new(0);
    }

    public DashboardServer(int port)
    {
        _port = port;
    }

    public string? LastError => _lastError;

    public static string NewToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(4))[..8];
    }

    public static string LanUrl(int port)
    {
        return $"http://{LanIp()}:{port}/";
    }

    private static string LanIp()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;
                var b = ua.Address.GetAddressBytes();
                if (b.Length == 4 && b[0] == 169 && b[1] == 254) continue;
                return ua.Address.ToString();
            }
        }
        return "127.0.0.1";
    }

    public bool Start()
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, _port);
            listener.Start(512);
            ActualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            Running = true;
            _ = Task.Run(async () =>
            {
                while (Running)
                {
                    try
                    {
                        var client = await listener.AcceptTcpClientAsync();
                        _ = Task.Run(() => HandleClient(client));
                    }
                    catch (ObjectDisposedException) { break; }
                    catch { }
                }
            });
            return true;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            return false;
        }
    }

    public void Stop()
    {
        Running = false;
        foreach (var c in _sseClients.Values)
        {
            try { c.Tcp?.Close(); } catch { }
        }
        _sseClients.Clear();
    }

    public void Dispose() => Stop();

    public void PushTranscript(string role, string text)
    {
        var entry = new LogEntry
        {
            T = DateTime.Now.ToString("HH:mm:ss"),
            Role = role switch { "user" => "user", "ultron" => "ultron", _ => "system" },
            Text = text,
        };
        var payload = JsonSerializer.Serialize(new
        {
            type = "msg",
            t = entry.T,
            role = entry.Role,
            text = entry.Text,
        }) + SseEventText;
        lock (_logLock)
        {
            _log.Add(entry);
            if (_log.Count > MaxLogEntries) _log.RemoveAt(0);
        }
        Broadcast(payload);
    }

    public void PushStatus(string state, bool awake)
    {
        _status = state;
        _awake = awake;
        Broadcast(JsonSerializer.Serialize(new { type = "status", state, awake }) + SseEventText);
    }

    public void PushConfidence(float score)
    {
        _confidence = Math.Clamp(score, 0f, 1f);
        Broadcast(JsonSerializer.Serialize(new { type = "conf", value = _confidence }) + SseEventText);
    }

    public void PushMute(bool on)
    {
        _muted = on;
        Broadcast(JsonSerializer.Serialize(new { type = "mute", on }) + SseEventText);
    }

    private void Broadcast(string payload)
    {
        if (!Running) return;
        foreach (var c in _sseClients.Values)
        {
            c.Queue.Enqueue(payload);
            try { c.Signal.Release(); } catch { }
        }
    }

    private async void HandleClient(TcpClient client)
    {
        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);

            var line = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(line)) return;
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] != "GET")
            {
                await Reply(stream, "400 Bad Request", "text/plain", "Bad request\r\n");
                return;
            }
            var target = parts[1];
            int cap = 0;
            while ((await reader.ReadLineAsync()) is { } h && h.Length > 0)
            {
                if (++cap > 40)
                {
                    await Reply(stream, "400 Bad Request", "text/plain", "Headers too long\r\n");
                    return;
                }
            }

            var targetParts = target.Split('?', 2);
            var path = targetParts[0];
            var query = targetParts.Length == 2 ? targetParts[1] : "";
            var tokenOk = GetToken(query) == Token;

            if (path is "/" or "/index.html")
            {
                if (!tokenOk) { await Reply(stream, "401 Unauthorized", "text/plain", "Bad token\r\n"); return; }
                await Reply(stream, "200 OK", "text/html; charset=utf-8", PageHtml);
            }
            else if (path == "/api/state")
            {
                if (!tokenOk) { await Reply(stream, "401 Unauthorized", "text/plain", "Bad token\r\n"); return; }
                await Reply(stream, "200 OK", "application/json; charset=utf-8", BuildSnapshot());
            }
            else if (path == "/api/stream")
            {
                if (!tokenOk) { await Reply(stream, "401 Unauthorized", "text/plain", "Bad token\r\n"); return; }
                await HandleSse(client, stream);
            }
            else if (path == "/favicon.ico")
            {
                await Reply(stream, "204 No Content", "text/plain", "");
            }
            else
            {
                await Reply(stream, "404 Not Found", "text/plain", "Not found\r\n");
            }
        }
        catch { }
        finally
        {
            try { client.Close(); } catch { }
        }
    }

    private static string GetToken(string query)
    {
        foreach (var kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = kv.IndexOf('=');
            if (eq > 0 && kv[..eq] == "k") return Uri.UnescapeDataString(kv[(eq + 1)..]);
        }
        return "";
    }

    private async Task HandleSse(TcpClient client, NetworkStream stream)
    {
        var id = Interlocked.Increment(ref _clientSeq);
        var c = new SseClient { Tcp = client, Stream = stream };
        _sseClients[id] = c;

        var head = "HTTP/1.1 200 OK\r\n" +
                   "Content-Type: text/event-stream\r\n" +
                   "Cache-Control: no-cache, no-transform\r\n" +
                   "Connection: keep-alive\r\n" +
                   "Access-Control-Allow-Origin: *\r\n\r\n";
        var headBytes = Encoding.UTF8.GetBytes(head);
        await stream.WriteAsync(headBytes);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("data: " + BuildSnapshot() + "\n\n"));
        await stream.FlushAsync();

        using var ct = new CancellationTokenSource(TimeSpan.FromHours(12));
        try
        {
            while (Running && !ct.IsCancellationRequested)
            {
                var waitSignal = c.Signal.WaitAsync(ct.Token);
                var ping = Task.Delay(15000, ct.Token);
                var done = await Task.WhenAny(waitSignal, ping);
                if (done == ping)
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(": ping\n\n"));
                    await stream.FlushAsync();
                    continue;
                }
                if (waitSignal.IsCanceled) break;
                while (c.Queue.TryDequeue(out var ev))
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes("data: " + ev + "\n\n"));
                }
                await stream.FlushAsync();
                while (c.Signal.Wait(0)) { }
            }
        }
        catch { }
        finally
        {
            _sseClients.TryRemove(id, out _);
        }
    }

    private string BuildSnapshot()
    {
        lock (_logLock)
        {
            return JsonSerializer.Serialize(new
            {
                type = "snapshot",
                token = Token,
                status = _status,
                awake = _awake,
                muted = _muted,
                confidence = _confidence,
                messages = _log.Select(e => (object)new { t = e.T, role = e.Role, text = e.Text }).ToArray(),
            });
        }
    }

    private static async Task Reply(NetworkStream stream, string status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes($"HTTP/1.1 {status}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n" + body);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private const string PageHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>ULTRON DECK</title>
        <style>
          :root{--bg:#0d0e12;--panel:#14151b;--line:#23252c;--txt:#e7e9ee;--dim:#9aa0ab;
                --crimson:#ff2e2e;--amber:#ffb703;--teal:#2ee6c8;--violet:#a78bfa}
          *{box-sizing:border-box}
          body{margin:0;background:var(--bg);color:var(--txt);
               font:14px/1.5 ui-monospace,"Cascadia Mono",Consolas,monospace}
          header{position:sticky;top:0;z-index:2;display:flex;gap:10px;align-items:center;
                 flex-wrap:wrap;padding:10px 14px;background:rgba(13,14,18,.92);
                 border-bottom:1px solid var(--line)}
          h1{font-size:15px;letter-spacing:.28em;margin:0;color:var(--crimson);
             text-shadow:0 0 12px rgba(255,46,46,.45)}
          .badge{padding:3px 9px;border:1px solid var(--line);border-radius:999px;font-size:11px;
                 color:var(--dim);letter-spacing:.08em}
          .badge.on{border-color:rgba(162,235,173,.35);color:#a2ebad}
          .badge.off{border-color:rgba(255,46,46,.4);color:#ff7d7d}
          .badge.hot{border-color:rgba(255,183,3,.5);color:var(--amber)}
          #confwrap{flex:1;min-width:140px;height:18px;border:1px solid var(--line);
                    border-radius:999px;overflow:hidden;position:relative}
          #conf{height:100%;width:0%;background:linear-gradient(90deg,var(--crimson),var(--amber));transition:width .25s}
          #conflbl{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;
                   font-size:10px;color:#fff;letter-spacing:.1em}
          main{max-width:860px;margin:0 auto;padding:14px}
          .msg{margin:0 0 10px;padding:9px 12px;border:1px solid var(--line);
               border-left:3px solid var(--dim);border-radius:6px;background:var(--panel)}
          .msg .t{display:block;font-size:10px;color:var(--dim);letter-spacing:.08em;margin-bottom:2px}
          .msg.user{border-left-color:var(--teal)}
          .msg.ultron{border-left-color:var(--amber)}
          .msg.system{border-left-color:var(--violet);opacity:.75}
          .wht {white-space:pre-wrap;word-break:break-word;margin:0}
          footer{padding:10px 14px;color:var(--dim);font-size:11px;text-align:center;
                 border-top:1px solid var(--line)}
          #empty{color:var(--dim);text-align:center;padding:40px 0}
        </style>
        </head>
        <body>
        <header>
          <h1>ULTRON DECK</h1>
          <span class="badge" id="st">CORE: --</span>
          <span class="badge" id="aw">AWAKE: --</span>
          <span class="badge" id="mu">MIC: --</span>
          <span id="confwrap"><span id="conf"></span><span id="conflbl">0%</span></span>
        </header>
        <main id="log"><div id="empty">Waiting for the first exchange…</div></main>
        <footer>read-only LAN mirror · token is in the QR · auto-reconnects</footer>
        <script>
          var esc=document.createElement('div');
          function h(s){esc.textContent=s;return esc.innerHTML;}
          var st=document.getElementById('st'),aw=document.getElementById('aw'),
              mu=document.getElementById('mu'),conf=document.getElementById('conf'),
              conflbl=document.getElementById('conflbl'),log=document.getElementById('log');
          function badge(el,on){el.classList.remove('on','off','hot');
              el.classList.add(on==='hot'?'hot':(on?'on':'off'));}
          function add(t,role,text){
            var d=document.getElementById('empty'); if(d)d.remove();
            var m=document.createElement('div');m.className='msg '+role;
            var ts=document.createElement('span');ts.className='t';ts.textContent=t;
            var p=document.createElement('p');p.className='wht';p.innerHTML=h(text);
            m.appendChild(ts);m.appendChild(p);log.appendChild(m);
            while(log.children.length>250)log.removeChild(log.firstChild);
            log.scrollTop=log.scrollHeight;}
          function confpct(v){conf.style.width=(v*100).toFixed(0)+'%';
              conflbl.textContent=(v*100).toFixed(0)+'%';}
          var url='/api/stream'+(location.search||'');
          var ev=new EventSource(url);
          ev.onmessage=function(e){
            try{var d=JSON.parse(e.data);}
            catch(_){return;}
            if(d.type==='snapshot'){
              st.textContent='CORE: '+(d.status||'offline').toUpperCase();
              badge(aw,!!d.awake);badge(mu,!!d.muted);confpct(d.confidence||0);
              if(d.messages)for(var i=0;i<d.messages.length;i++)
                add(d.messages[i].t,d.messages[i].role,d.messages[i].text);
            }else if(d.type==='msg'){
              add(d.t,d.role,d.text);
            }else if(d.type==='status'){
              st.textContent='CORE: '+(d.state||'offline').toUpperCase();
              badge(aw,!!d.awake);
            }else if(d.type==='conf'){confpct(d.value);}
            else if(d.type==='mute'){badge(mu,!!d.on);mu.textContent='MIC: '+(d.on?'MUTED':'LIVE');}
          };
          ev.onerror=function(){st.textContent='CORE: RECONNECTING';};
        </script>
        </body>
        </html>
        """;
}