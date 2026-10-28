using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xrmpe.Launcher;

void Assert(bool condition,string label) { if(!condition)throw new Exception(label); }
await FailoverChecks.Run();
await LoopbackCatalog.Test();
foreach(var bad in new[]{"http://example.com","https://user:password@example.com","https://example.com/x","https://example.com/?token=secret"})
{try{GlobalCatalog.ValidateAddress(bad);throw new Exception("Accepted URL");}catch(ArgumentException){}}
foreach(var bad in new[]{"127.0.0.1","10.1.1.1","192.168.1.1","100.64.0.1","224.0.0.1","::1"})Assert(!GlobalCatalog.PublicIPv4(bad),"Private catalogue target");
async Task ResponseTest(string body,bool reject,int expected=0,int status=200)
{
 var tcp=new TcpListener(IPAddress.Loopback,0);tcp.Start();
 var port=((IPEndPoint)tcp.LocalEndpoint).Port;
 var serve=Task.Run(async()=>{using var peer=await tcp.AcceptTcpClientAsync();await using var stream=peer.GetStream();using var reader=new StreamReader(stream,Encoding.ASCII,leaveOpen:true);while(await reader.ReadLineAsync() is {Length:>0}){};var bytes=Encoding.UTF8.GetBytes(body);var header=Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Length: {bytes.Length}\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n");try{await stream.WriteAsync(header);await stream.WriteAsync(bytes);}catch(IOException){}});
 try{using var client=new GlobalCatalog("http://127.0.0.1:"+port);using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(5));
  bool rejected=false;try{var result=await client.DiscoverAsync(cancel.Token);Assert(result.Count==expected,"Response filtering");}catch(Exception ex)when(ex is System.Text.Json.JsonException or InvalidDataException or HttpRequestException){rejected=true;}
  Assert(rejected==reject,"Malformed/oversize/unavailable response rejection");await serve.WaitAsync(TimeSpan.FromSeconds(5));
 }finally{tcp.Stop();}
}
await ResponseTest("not-json",true);
await ResponseTest(new string('x',140000),true);
await ResponseTest("unavailable",true,status:503);
var expires=DateTimeOffset.UtcNow.ToUnixTimeSeconds()+120;
await ResponseTest($$"""{"schema":1,"servers":[{"host":"10.0.0.1","port":5446,"queryPort":5445,"steam":false,"expires":{{expires}}},{"host":"8.8.4.4","port":5446,"queryPort":5445,"steam":false,"expires":{{expires}}},{"host":"8.8.8.8","port":5446,"queryPort":5445,"steam":false,"expires":1}]}""",false,1);
Console.WriteLine("PASS: malformed JSON, oversized body and HTTP 503 rejected; private and expired entries filtered without probing them.");
var start=new ProcessStartInfo("node"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
start.ArgumentList.Add(Path.GetFullPath("services/global-catalog/local.mjs"));start.ArgumentList.Add("0");
using var process=Process.Start(start)!;var stderr=process.StandardError.ReadToEndAsync();
try {
 var address=await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
 using var catalog=new GlobalCatalog(address!);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
 using var game=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));int query=((IPEndPoint)game.Client.LocalEndPoint!).Port;
 using var responderStop=new CancellationTokenSource();
 var responder=Task.Run(async()=>{try{while(!responderStop.IsCancellationRequested){var packet=await game.ReceiveAsync(responderStop.Token);if(packet.Buffer.Length<7||packet.Buffer[2]!=0)continue;var fields=Encoding.UTF8.GetBytes("hostname\0TEST-QR2\0hostport\05446\0gamename\0stalker\0mapname\0test_map\0gametype\0deathmatch\0numplayers\02\0maxplayers\032\0\0");var reply=new byte[]{0}.Concat(packet.Buffer.Skip(3).Take(4)).Concat(fields).ToArray();await game.SendAsync(reply,packet.RemoteEndPoint,responderStop.Token);}}catch(OperationCanceledException){}});
 bool alive=true;using var publicationStop=new CancellationTokenSource();var statuses=new List<string>();
 var publication=catalog.PublishWhileAlive(5446,query,false,()=>alive,s=>{lock(statuses)statuses.Add(s);},publicationStop.Token);
 IReadOnlyList<BrowserServer> list=[];
 for(int i=0;i<40;i++){list=await catalog.DiscoverAsync(timeout.Token);if(list.Count>0)break;await Task.Delay(100,timeout.Token);}
 Assert(list.Count==1 && list[0].Internet && list[0].Snapshot is null && list[0].Ping is null,"Heartbeat does not assert availability or stats");
 var verified=await Qr2Query.ProbeAsync(list[0],timeout.Token);
 Assert(verified.Snapshot is {Name:"TEST-QR2",Players:2,MaxPlayers:32} && verified.Ping>=0,"Actual UDP QR2 stats and RTT");
 alive=false;await publication.WaitAsync(TimeSpan.FromSeconds(8));
 Assert((await catalog.DiscoverAsync(timeout.Token)).Count==0,"Process death removes registration");
 responderStop.Cancel();await responder;
 Console.WriteLine("PASS: actual Node/SQLite HTTP catalogue, C# client, publication while alive, real UDP QR2/RTT, no invented stats, process-death withdrawal, private URL/IP validation.");
}finally{if(!process.HasExited)process.Kill(true);await process.WaitForExitAsync();await stderr;}
