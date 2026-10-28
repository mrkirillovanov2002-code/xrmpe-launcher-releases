using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xrmpe.Launcher;

sealed class LoopbackCatalog : IAsyncDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback,0);
    readonly CancellationTokenSource _stop=new();
    readonly Task _loop;
    public int Status=200;
    public int Calls;
    readonly string _host;
    public string Address {get;}
    public LoopbackCatalog(string host)
    {
        _host=host;_listener.Start();Address="http://127.0.0.1:"+((IPEndPoint)_listener.LocalEndpoint).Port;_loop=Run();
    }
    async Task Run()
    {
        try
        {
            while(!_stop.IsCancellationRequested)
            {
                using var peer=await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream=peer.GetStream();using var reader=new StreamReader(stream,Encoding.ASCII,leaveOpen:true);
                while(await reader.ReadLineAsync(_stop.Token) is {Length:>0}){}
                Interlocked.Increment(ref Calls);
                if(Status<0)continue; // Actual dropped TCP connection, not a manufactured client exception.
                var body=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{schema=1,servers=new[]{new{host=_host,port=5446,queryPort=5445,steam=false,expires=DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds()}}}));
                var header=Encoding.ASCII.GetBytes($"HTTP/1.1 {Status} Test\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header,_stop.Token);await stream.WriteAsync(body,_stop.Token);
            }
        }catch(Exception ex)when(ex is OperationCanceledException or SocketException or IOException) { if(!_stop.IsCancellationRequested)throw; }
    }
    public async ValueTask DisposeAsync(){_stop.Cancel();_listener.Stop();await _loop;_stop.Dispose();}
    public static async Task Test()
    {
        await using var primary=new LoopbackCatalog("8.8.8.8");await using var backup=new LoopbackCatalog("1.1.1.1");
        var now=DateTimeOffset.UtcNow;using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var cluster=new CatalogCluster(new(primary.Address,false,backup.Address),clock:()=>now);
        if((await cluster.DiscoverAsync(ct.Token)).Count!=2)throw new Exception("Actual HTTP union");
        primary.Status=-1;now=now.AddSeconds(16);
        if((await cluster.DiscoverAsync(ct.Token)).Count!=1)throw new Exception("Actual TCP close fallback");
        var count=primary.Calls;now=now.AddSeconds(16);await cluster.DiscoverAsync(ct.Token);
        if(primary.Calls!=count)throw new Exception("Network backoff");
        primary.Status=200;now=now.AddSeconds(20);
        if((await cluster.DiscoverAsync(ct.Token)).Count!=2)throw new Exception("Actual HTTP recovery");
        primary.Status=-1;backup.Status=503;now=now.AddSeconds(16);
        bool failed=false;try{await cluster.DiscoverAsync(ct.Token);}catch(HttpRequestException){failed=true;}
        if(!failed)throw new Exception("Actual both-down failure");
        Console.WriteLine("PASS: two real loopback HTTP servers, dropped TCP connection fallback, both-down handling, cooldown and automatic primary recovery.");
    }
}
