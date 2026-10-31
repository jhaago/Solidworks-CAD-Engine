using System.Diagnostics;
using Newtonsoft.Json.Linq;
using SolidWorksCadEngine.Adapter;

internal static class Program
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static async Task<T> Throws<T>(Func<Task> work) where T : Exception
    { try { await work(); } catch (T e) { return e; } throw new Exception("Expected " + typeof(T).Name); }
    static string Fresh() { var p = Path.Combine(Path.GetTempPath(), "cad-adapter-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
    static EngineOptions Options(string mode = "normal") => new EngineOptions {
        Enabled = true, FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
        Arguments = "\"" + typeof(Program).Assembly.Location + "\" --fixture " + mode,
        RequestTimeout = TimeSpan.FromSeconds(3)
    };
    static FileCadExecutionBoundary Boundary(string root) => new(Path.Combine(root, "cad.lock"), "synthetic-job", Path.Combine(root, "journal.jsonl"));
    static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "--fixture") return await Fixture(args.ElementAtOrDefault(1) ?? "normal");
        var tests = new (string, Func<Task>)[] {
            ("opt-in required", async () => { var o=Options();o.Enabled=false; await Throws<InvalidOperationException>(()=>McpBackendSession.StartAsync(o,Boundary(Fresh()))); }),
            ("initialization, paginated schemas and lossless results", async () => {
                var root=Fresh(); using var s=await McpBackendSession.StartAsync(Options(),Boundary(root));
                Check(s.ServerInfo["serverInfo"]?["name"]?.ToString()=="solidworks-mcp","server identity");
                Check(s.Tools.Count==4 && s.Tools["model_change"]["inputSchema"]?["properties"]?["mode"]!=null,"pagination/schema");
                var r=await s.CallToolAsync("model_change",new JObject());
                Check(r["structuredContent"]?["feature"]?.ToString()=="Plate" && r["content"]?.Count()==2 && r["_meta"]?["opaque"]?.Value<int>()==42,"raw result lost");
                Check(!File.Exists(Path.Combine(root,"cad.lock.recovery-required")),"success did not clear marker");
                Check(File.ReadAllLines(Path.Combine(root,"journal.jsonl")).Length==2,"dispatch/result journal");
            }),
            ("scripting and unknown tools blocked before dispatch", async () => {
                var root=Fresh();using var s=await McpBackendSession.StartAsync(Options(),Boundary(root));
                await Throws<InvalidOperationException>(()=>s.CallToolAsync("vba_run",new JObject()));
                await Throws<ArgumentException>(()=>s.CallToolAsync("unknown",new JObject()));
                Check(!File.Exists(Path.Combine(root,"journal.jsonl")),"blocked tool dispatched");
                var r=await s.CallToolAsync("sw_status",new JObject());Check(r["structuredContent"]?["vba"]?.ToString()=="off","VBA inherited");
            }),
            ("tool errors preserved", async () => {
                using var s=await McpBackendSession.StartAsync(Options(),Boundary(Fresh()));
                var r=await s.CallToolAsync("model_change",new JObject{{"mode","tool-error"}});
                Check(r["isError"]?.Value<bool>()==true && r["content"]?[0]?["text"]?.ToString()=="INVALID_ARGUMENT: bad value","tool error lost");
                await s.CallToolAsync("sw_status",new JObject());
            }),
            ("RPC errors preserve data and prevent replay", async () => {
                var root=Fresh(); using var s=await McpBackendSession.StartAsync(Options(),Boundary(root));
                var e=await Throws<McpRpcException>(()=>s.CallToolAsync("model_change",new JObject{{"mode","rpc-error"}}));
                Check(e.Error["data"]?["detail"]?.ToString()=="retained","RPC data lost");
                await Throws<UncertainOutcomeException>(()=>s.CallToolAsync("model_change",new JObject()));
                Check(File.ReadAllLines(Path.Combine(root,"journal.jsonl")).Count(l=>JObject.Parse(l)["event"]?.ToString()=="dispatch")==1,"operation replayed");
            }),
            ("timeout quarantines next session", async () => {
                var root=Fresh(); using(var s=await McpBackendSession.StartAsync(Options(),Boundary(root)))
                    await Throws<UncertainOutcomeException>(()=>s.CallToolAsync("model_change",new JObject{{"mode","hang"}}));
                Check(File.Exists(Path.Combine(root,"cad.lock.recovery-required")),"missing recovery marker");
                await Throws<InvalidOperationException>(()=>Task.Run(()=>{using var b=Boundary(root);}));
            }),
            ("cancellation after dispatch quarantines", async () => {
                var root=Fresh();using var s=await McpBackendSession.StartAsync(Options(),Boundary(root));using var c=new CancellationTokenSource(150);
                await Throws<UncertainOutcomeException>(()=>s.CallToolAsync("model_change",new JObject{{"mode","hang"}},c.Token));
                Check(File.Exists(Path.Combine(root,"cad.lock.recovery-required")),"cancelled outcome not quarantined");
            }),
            ("queued cancellation does not dispatch or poison active call", async () => {
                var root=Fresh();using var s=await McpBackendSession.StartAsync(Options(),Boundary(root));
                var first=s.CallToolAsync("model_change",new JObject{{"mode","slow"}});using var c=new CancellationTokenSource(50);
                await Throws<OperationCanceledException>(()=>s.CallToolAsync("model_change",new JObject(),c.Token));
                await first;await s.CallToolAsync("sw_status",new JObject());
                Check(File.ReadAllLines(Path.Combine(root,"journal.jsonl")).Count(l=>JObject.Parse(l)["event"]?.ToString()=="dispatch")==2,"queued call dispatched");
            }),
            ("unexpected worker exit is uncertain", async () => {
                using var s=await McpBackendSession.StartAsync(Options(),Boundary(Fresh()));
                await Throws<UncertainOutcomeException>(()=>s.CallToolAsync("model_change",new JObject{{"mode","exit"}}));
            }),
            ("server TIMEOUT is uncertain despite response", async () => {
                var root=Fresh();using var s=await McpBackendSession.StartAsync(Options(),Boundary(root));
                var r=await s.CallToolAsync("model_change",new JObject{{"mode","server-timeout"}});
                Check(r["isError"]?.Value<bool>()==true,"error lost");
                await Throws<UncertainOutcomeException>(()=>s.CallToolAsync("sw_status",new JObject()));
                Check(File.Exists(Path.Combine(root,"cad.lock.recovery-required")),"server timeout marker cleared");
            }),
            ("protocol mismatch rejected", async () => { await Throws<InvalidDataException>(()=>McpBackendSession.StartAsync(Options("bad-protocol"),Boundary(Fresh()))); }),
            ("repeated discovery cursor rejected", async () => { await Throws<InvalidDataException>(()=>McpBackendSession.StartAsync(Options("bad-cursor"),Boundary(Fresh()))); }),
            ("lease held for whole session", async () => {
                var root=Fresh();using(var s=await McpBackendSession.StartAsync(Options(),Boundary(root))) {
                    await Throws<IOException>(()=>Task.Run(()=>{using var b=Boundary(root);}));
                    await s.CallToolAsync("sw_status",new JObject());
                    await Throws<IOException>(()=>Task.Run(()=>{using var b=Boundary(root);}));
                }using var released=Boundary(root);
            }),
            ("journal failure prevents dispatch", async () => {
                var root=Fresh();var journal=Path.Combine(root,"journal.jsonl");Directory.CreateDirectory(journal);
                using var s=await McpBackendSession.StartAsync(Options(),Boundary(root));
                await Throws<UnauthorizedAccessException>(()=>s.CallToolAsync("model_change",new JObject()));
                Check(File.Exists(Path.Combine(root,"cad.lock.recovery-required")),"journal failure must fail closed");
            }),
            ("valid JSON malformed tool results fail closed", async () => {
                foreach(var mode in new[]{"empty-result","bad-content","bad-error-flag"}) {
                    var root=Fresh();using var s=await McpBackendSession.StartAsync(Options(),Boundary(root));
                    await Throws<UncertainOutcomeException>(()=>s.CallToolAsync("model_change",new JObject{{"mode",mode}}));
                    Check(File.Exists(Path.Combine(root,"cad.lock.recovery-required")),"malformed result cleared marker");
                }
            }),
            ("disposal retains lease when worker termination is unconfirmed", async () => {
                var root=Fresh();var factory=new ControlledFactory();var s=await McpBackendSession.StartAsync(Options(),Boundary(root),factory);
                try {
                await Throws<UncertainOutcomeException>(()=>Task.Run(()=>s.Dispose()));
                Check(!factory.Worker!.HasExited,"controlled worker exited early");
                await Throws<IOException>(()=>Task.Run(()=>{using var b=Boundary(root);}));
                Check(File.Exists(Path.Combine(root,"cad.lock.recovery-required")),"idle live worker not quarantined");
                factory.Worker.AllowStop=true;s.Dispose();
                await Throws<InvalidOperationException>(()=>Task.Run(()=>{using var b=Boundary(root);}));
                File.Delete(Path.Combine(root,"cad.lock.recovery-required"));using var released=Boundary(root);
                } finally { factory.Worker!.AllowStop=true;factory.Worker.RequestStop();factory.Worker.WaitForExit(2000);factory.Worker.Dispose(); }
            }),
            ("disposal cancels and drains an active request before lease release", async () => {
                var root=Fresh();var s=await McpBackendSession.StartAsync(Options(),Boundary(root));
                var call=s.CallToolAsync("model_change",new JObject{{"mode","hang"}});
                s.Dispose();await Throws<UncertainOutcomeException>(()=>call);
                await Throws<InvalidOperationException>(()=>Task.Run(()=>{using var b=Boundary(root);}));
            }),
            ("malformed worker stdout fails explicitly", async () => {
                using var s=await McpBackendSession.StartAsync(Options(),Boundary(Fresh()));
                await Throws<UncertainOutcomeException>(()=>s.CallToolAsync("model_change",new JObject{{"mode","malformed"}}));
            })
        };
        int failures=0;foreach(var (name,run) in tests) { try {await run();Console.WriteLine("PASS "+name);}catch(Exception e){failures++;Console.WriteLine("FAIL "+name+": "+e);} }
        Console.WriteLine($"Adapter tests: {tests.Length-failures}/{tests.Length} passed");return failures==0?0:1;
    }
    sealed class ControlledFactory : IEngineWorkerFactory {
        public ControlledWorker? Worker;
        public IEngineWorker Start(ProcessStartInfo start) {var p=new Process {StartInfo=start};p.Start();return Worker=new ControlledWorker(p);}
    }
    sealed class ControlledWorker(Process process) : IEngineWorker {
        public bool AllowStop;
        bool disposed;
        public bool HasExited=>disposed||process.HasExited;
        public TextReader Output=>process.StandardOutput;
        public TextReader Error=>process.StandardError;
        public TextWriter Input=>process.StandardInput;
        public void RequestStop(){if(!disposed&&AllowStop&&!process.HasExited)process.Kill();}
        public bool WaitForExit(int milliseconds)=>disposed||(AllowStop?process.WaitForExit(milliseconds):process.HasExited);
        public void Dispose(){if(!disposed&&process.HasExited){process.Dispose();disposed=true;}}
    }
    static async Task<int> Fixture(string mode)
    {
        bool initialized=false;
        while(await Console.In.ReadLineAsync() is string line) {
            var q=JObject.Parse(line);var method=q["method"]?.ToString();var id=q["id"];
            if(method=="notifications/initialized"){initialized=true;continue;}if(id==null)continue;
            JObject result;
            if(method=="initialize") result=new JObject{{"protocolVersion",mode=="bad-protocol"?"1900-01-01":"2025-06-18"},{"capabilities",new JObject{{"tools",new JObject()}}},{"serverInfo",new JObject{{"name","solidworks-mcp"},{"version","fixture"}}}};
            else if(method=="tools/list") {
                Check(initialized,"initialized notification absent");bool second=q["params"]?["cursor"]!=null;
                string[] names=second?new[]{"feature_other","vba_run"}:new[]{"model_change","sw_status"};
                result=new JObject{{"tools",new JArray(names.Select(n=>new JObject{{"name",n},{"inputSchema",new JObject{{"type","object"},{"properties",new JObject{{"mode",new JObject{{"type","string"}}}}}}}}))}};
                if(!second||mode=="bad-cursor")result["nextCursor"]="page2";
            } else {
                string behavior=q["params"]?["arguments"]?["mode"]?.ToString()??"";
                if(behavior=="exit")return 23;
                if(behavior=="hang")await Task.Delay(30000);
                if(behavior=="slow")await Task.Delay(300);
                if(behavior=="malformed"){Console.WriteLine("not json");await Console.Out.FlushAsync();continue;}
                if(behavior=="rpc-error") {Console.WriteLine(new JObject{{"jsonrpc","2.0"},{"id",id},{"error",new JObject{{"code",-32001},{"message","worker failed"},{"data",new JObject{{"detail","retained"}}}}}}.ToString(Newtonsoft.Json.Formatting.None));continue;}
                bool error=behavior is "tool-error" or "server-timeout";
                result=new JObject{{"isError",error},{"content",new JArray(new JObject{{"type","text"},{"text",error?(behavior=="server-timeout"?"TIMEOUT: may finish":"INVALID_ARGUMENT: bad value"):"{\"ok\":true}"}},new JObject{{"type","image"},{"mimeType","image/png"},{"data","opaque"}})},
                    {"structuredContent",new JObject{{"feature","Plate"},{"vba",Environment.GetEnvironmentVariable("SWMCP_VBA")}}},{"_meta",new JObject{{"opaque",42}}}};
                if(behavior=="empty-result")result=new JObject();
                if(behavior=="bad-content")result["content"]=new JObject();
                if(behavior=="bad-error-flag")result["isError"]="true";
                Console.Error.WriteLine("fixture diagnostic");
                Console.WriteLine(new JObject{{"jsonrpc","2.0"},{"method","notifications/progress"},{"params",new JObject()}}.ToString(Newtonsoft.Json.Formatting.None));
                Console.WriteLine(new JObject{{"jsonrpc","2.0"},{"id",-999},{"result",new JObject()}}.ToString(Newtonsoft.Json.Formatting.None));
            }
            Console.WriteLine(new JObject{{"jsonrpc","2.0"},{"id",id},{"result",result}}.ToString(Newtonsoft.Json.Formatting.None));await Console.Out.FlushAsync();
        }return 0;
    }
}
