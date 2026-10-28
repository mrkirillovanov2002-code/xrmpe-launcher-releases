using System.Net;
using System.Text.Json;
using Xrmpe.Launcher;

static class FailoverChecks
{
    sealed class Handler : HttpMessageHandler
    {
        public int Count;
        public Func<HttpRequestMessage, HttpResponseMessage> Reply = _ => List("8.8.8.8");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        { Interlocked.Increment(ref Count); return Task.FromResult(Reply(request)); }
    }
    static HttpResponseMessage List(params string[] hosts) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { schema=1, servers=hosts.Select(host=>new {host,port=5446,queryPort=5445,steam=false,expires=DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds()}) })) };
    static void Assert(bool condition,string label){if(!condition)throw new Exception(label);}
    public static async Task Run()
    {
        var a=new Handler();var b=new Handler { Reply=_=>List("8.8.8.8","1.1.1.1") };
        var now=DateTimeOffset.UtcNow;
        using var cluster=new CatalogCluster(new("https://primary.test",false,"https://backup.test"),u=>new GlobalCatalog(u,u.Contains("primary")?a:b),()=>now);
        Assert((await cluster.DiscoverAsync(default)).Count==2,"Healthy merge and dedup");
        int count=a.Count+b.Count;await cluster.DiscoverAsync(default);Assert(a.Count+b.Count==count,"Click cache");
        a.Reply=_=>throw new HttpRequestException("Injected connection closed");now=now.AddSeconds(16);
        Assert((await cluster.DiscoverAsync(default)).Count==2 && cluster.Status.Contains("Текущий источник: Резервный"),"Fallback");
        int primaryCalls=a.Count;now=now.AddSeconds(16);await cluster.DiscoverAsync(default);Assert(a.Count==primaryCalls,"Primary cooldown");
        a.Reply=_=>List("9.9.9.9");now=now.AddSeconds(20);
        Assert((await cluster.DiscoverAsync(default)).Count==3 && cluster.Status.Contains("Источник: Основной"),"Recovered primary + union");
        a.Reply=_=>throw new TaskCanceledException("Injected timeout");b.Reply=_=>throw new HttpRequestException("Down");now=now.AddSeconds(16);
        bool refused=false;try{await cluster.DiscoverAsync(default);}catch(HttpRequestException){refused=true;}Assert(refused,"Both down");
        foreach(var status in new[]{400,401,409,404,429})
        {
            var hard=new Handler{Reply=_=>new HttpResponseMessage((HttpStatusCode)status)};var reserve=new Handler();
            using var isolated=new CatalogCluster(new("https://primary.test",false,"https://backup.test"),u=>new GlobalCatalog(u,u.Contains("primary")?hard:reserve));
            try{await isolated.DiscoverAsync(default);throw new Exception("Expected HTTP rejection");}catch(HttpRequestException){}
            Assert(reserve.Count==0,"No failover on HTTP "+status);
        }
        foreach(var status in new[]{500,502,503,504,408})
        {
            var transient=new Handler{Reply=_=>new HttpResponseMessage((HttpStatusCode)status)};var reserve=new Handler();
            using var isolated=new CatalogCluster(new("https://primary.test",false,"https://backup.test"),u=>new GlobalCatalog(u,u.Contains("primary")?transient:reserve));
            Assert((await isolated.DiscoverAsync(default)).Count==1,"Transient HTTP fallback "+status);
        }
        var timeout=new Handler{Reply=_=>throw new TaskCanceledException("Injected timeout")};var good=new Handler();
        using(var isolated=new CatalogCluster(new("https://primary.test",false,"https://backup.test"),u=>new GlobalCatalog(u,u.Contains("primary")?timeout:good)))
            Assert((await isolated.DiscoverAsync(default)).Count==1,"Timeout fallback");
        var work=Path.Combine(Path.GetTempPath(),"catalog-settings-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
        try
        {
            var path=Path.Combine(work,"global-catalog.json");File.WriteAllText(path,"{\"Address\":\"https://primary.test\",\"Publish\":true}");
            Assert(new CatalogPreferencesStore(path).Load().BackupAddress=="","Old settings migration");
            var expected=new CatalogPreferences("https://primary.test",true,"https://backup.test");new CatalogPreferencesStore(path).Save(expected);
            Assert(new CatalogPreferencesStore(path).Load()==expected,"Settings restart");
            var before=File.ReadAllBytes(path);try{new CatalogPreferencesStore(path).Save(expected with {BackupAddress=expected.Address});throw new Exception("Duplicate addresses accepted");}catch(ArgumentException){}
            Assert(File.ReadAllBytes(path).SequenceEqual(before),"Bad setting preserves file");
        }finally{Directory.Delete(work,true);}
        // Independent session capabilities and independent retries; one source failing cannot stop the other.
        var tokens=new Dictionary<string,string>();var posts=new Dictionary<string,int>();var deletes=new HashSet<string>();var sync=new object();
        Handler Publisher(string name,bool initiallyFailing) => new(){Reply=request=>
        {
            lock(sync)
            {
                var token=request.Headers.Authorization?.Parameter??"";
                Assert(token.Length==64,"Session capability");
                if(tokens.TryGetValue(name,out var prior))Assert(prior==token,"Retry retains lease capability");else tokens[name]=token;
                if(request.Method==HttpMethod.Delete){deletes.Add(name);return new(HttpStatusCode.OK);}
                posts[name]=posts.GetValueOrDefault(name)+1;
                if(initiallyFailing && posts[name]<=2)return new(HttpStatusCode.ServiceUnavailable);
                return new(HttpStatusCode.OK);
            }
        }};
        var pubA=Publisher("a",false);var pubB=Publisher("b",true);bool alive=true;
        using(var publishing=new CatalogCluster(new("https://primary.test",true,"https://backup.test"),u=>new GlobalCatalog(u,u.Contains("primary")?pubA:pubB,new(TimeSpan.Zero,TimeSpan.FromMilliseconds(25),TimeSpan.FromMilliseconds(5)))))
        {
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var task=publishing.PublishWhileAlive(5446,5445,false,()=>alive,_=>{},stop.Token);
            for(int i=0;i<200;i++){lock(sync){if(posts.GetValueOrDefault("a")>=4&&posts.GetValueOrDefault("b")>=4)break;}await Task.Delay(5);}
            alive=false;await task;
            Assert(posts["a"]>=4&&posts["b"]>=4,"Both publish after reserve recovery");
            Assert(tokens["a"]!=tokens["b"],"Capabilities isolated between services");Assert(deletes.SetEquals(new[]{"a","b"}),"Delete both services");
        }
        Console.WriteLine("PASS: primary/reserve/dual outage, recovery/backoff, merged partial lists/dedup, HTTP 400/401/409 no switch, 5xx/timeouts fallback, independent publication/retry/capabilities/deletion, settings migration/restart.");
    }
}
