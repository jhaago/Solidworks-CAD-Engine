using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadEngine.Adapter;

static class Program
{
    static string Required(string[] args,string flag) {
        int i=Array.IndexOf(args,flag);if(i<0||i+1>=args.Length)throw new ArgumentException("Required: "+flag+" <value>");return args[i+1];
    }
    static void Require(bool condition,string message){if(!condition)throw new InvalidDataException(message);}
    static void Near(double? actual,double expected,double tolerance,string message) => Require(actual.HasValue&&double.IsFinite(actual.Value)&&Math.Abs(actual.Value-expected)<=tolerance,message+": expected "+expected+", got "+actual);
    static async Task<int> Main(string[] args)
    {
        if(args.Length==0){Console.WriteLine("--engine <exe-or-dll> --output <new-directory> [--native]");return 2;}
        try {
            string engine=Path.GetFullPath(Required(args,"--engine"));string output=Path.GetFullPath(Required(args,"--output"));
            Require(!Directory.Exists(output)&&!File.Exists(output),"Use a new evidence directory; existing results are never overwritten.");Directory.CreateDirectory(output);
            string job=Guid.NewGuid().ToString("N");
            var options=new EngineOptions {Enabled=true,FileName=engine.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)?"dotnet":engine,
                Arguments=engine.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)?"\""+engine+"\"":"",RequestTimeout=TimeSpan.FromSeconds(90)};
            using var boundary=new FileCadExecutionBoundary(FileCadExecutionBoundary.DefaultLeasePath,job,Path.Combine(output,"journal.jsonl"));
            using var session=await McpBackendSession.StartAsync(options,boundary);
            var exercised=new List<string>();
            File.WriteAllText(Path.Combine(output,"catalog.json"),new JArray(session.Tools.Values).ToString(Formatting.Indented));
            File.WriteAllText(Path.Combine(output,"initialize.json"),session.ServerInfo.ToString(Formatting.Indented));
            string? doc=null;int call=0;
            async Task<JObject> Raw(string tool,JObject? input=null) {
                var parameters=input??new JObject();if(doc!=null)parameters["doc"]=doc;
                var result=await session.CallToolAsync(tool,parameters);exercised.Add(tool);
                File.WriteAllText(Path.Combine(output,$"{++call:D3}-{tool}.json"),result.ToString(Formatting.Indented));
                if(result["isError"]?.Value<bool>()==true)throw new InvalidOperationException(tool+": "+result.ToString(Formatting.None));
                Console.WriteLine(tool);return result;
            }
            async Task<JObject> Call(string tool,JObject? input=null) {
                var r=await Raw(tool,input);if(r["structuredContent"] is JObject structured)return structured;
                var text=(r["content"] as JArray)?.FirstOrDefault(b=>(string?)b["type"]=="text")?["text"]?.ToString();
                return JObject.Parse(text??throw new InvalidDataException("No tool JSON payload: "+tool));
            }
            JObject Ref(string name)=>new(){{"name",name}};
            await Call("sw_status");
            if(!args.Contains("--native")){Console.WriteLine($"REAL MCP PROBE PASSED: {session.Tools.Count} tools discovered; sw_status invoked. No modelling requested.");return 0;}
            Require(OperatingSystem.IsWindows(),"Native acceptance requires Windows and a licensed running SOLIDWORKS.");
            foreach(string tool in new[]{"doc_new","sketch_start","sketch_add","sketch_dimension_add","dimension_rename","dimension_set","doc_rebuild","model_info","doc_save"})
                Require(session.Tools.ContainsKey(tool),"Engine is missing acceptance tool: "+tool);
            var created=await Call("doc_new",new JObject{{"type","part"},{"name","adapter_plate_"+job},{"units","mmgs"}});
            doc=created["document"]?["handle"]?.ToString()??throw new InvalidDataException("New document handle absent.");
            Require((string?)created["document"]?["access"]=="owned","Acceptance document is not engine-owned.");
            async Task<string> Dimension(JArray entities,string kind,double value,string name){
                var r=await Call("sketch_dimension_add",new JObject{{"entities",entities},{"kind",kind},{"valueMm",value}});
                return (await Call("dimension_rename",new JObject{{"name",r["dimension"]?["name"]},{"newName",name}}))["dimension"]!["name"]!.ToString();
            }
            string profile=(await Call("sketch_start",new JObject{{"on",Ref("top")}}))["sketch"]!.ToString();
            await Call("feature_rename",new JObject{{"feature",profile},{"newName","MountingProfile"}});
            var rectangle=await Call("sketch_add",new JObject{{"entities",new JArray(new JObject{{"kind","rectangle"},{"center",new JArray(0,0)},{"widthMm",120},{"heightMm",60}})}});
            var sides=rectangle["entities"]![0]!["sides"]!;
            string width=await Dimension(new JArray(Ref(sides["bottom"]!.ToString())),"horizontal",120,"PlateWidth");
            await Dimension(new JArray(Ref(sides["left"]!.ToString())),"vertical",60,"PlateHeight");
            var boss=await Call("feature_extrude",new JObject{{"depthMm",8},{"name","PlateBody"}});
            var depth=(boss["dimensions"] as JArray)?.Single(d=>(string?)d["unit"]=="mm"&&Math.Abs(d["value"]!.Value<double>()-8)<1e-6);
            Require(depth!=null,"Expected one 8 mm extrusion driving dimension.");
            await Call("dimension_rename",new JObject{{"name",depth!["name"]},{"newName","PlateThickness"}});
            string holeSketch=(await Call("sketch_start",new JObject{{"on",Ref("top")}}))["sketch"]!.ToString();
            await Call("feature_rename",new JObject{{"feature",holeSketch},{"newName","MountingHole"}});
            var circle=await Call("sketch_add",new JObject{{"entities",new JArray(new JObject{{"kind","circle"},{"center",new JArray(20,10)},{"diameterMm",8}})}});
            await Dimension(new JArray(Ref(circle["entities"]![0]!["segments"]![0]!.ToString())),"diameter",8,"HoleDiameter");
            var center=new JObject{{"at",new JArray(20,10)}};
            await Dimension(new JArray(Ref("origin"),center.DeepClone()),"horizontal",20,"HoleX");
            await Dimension(new JArray(Ref("origin"),center.DeepClone()),"vertical",10,"HoleY");
            await Call("feature_extrude",new JObject{{"cut",true},{"end","through_all_both"},{"name","ThroughHole"}});
            async Task<JObject> Inspect(double w){
                await Call("doc_rebuild",new JObject{{"force",false}});
                var info=await Call("model_info");Near(info["volumeMm3"]?.Value<double>(),(w*60-Math.PI*16)*8,0.1,"native volume");
                Near(info["box"]?["size"]?[0]?.Value<double>(),w,0.1,"width");Near(info["box"]?["size"]?[1]?.Value<double>(),8,0.1,"thickness");Near(info["box"]?["size"]?[2]?.Value<double>(),60,0.1,"height");
                Require(info["bodies"]?.Count()==1,"Expected one solid body.");
                var dims=(await Call("dimension_list"))["dimensions"] as JArray??throw new InvalidDataException("No driving dimensions.");
                foreach(var wanted in new[]{("PlateWidth@MountingProfile",w),("PlateHeight@MountingProfile",60d),("PlateThickness@PlateBody",8d),("HoleDiameter@MountingHole",8d),("HoleX@MountingHole",20d),("HoleY@MountingHole",10d)}){
                    var dim=dims.Single(d=>(string?)d["name"]==wanted.Item1);Require(dim["driven"]?.Value<bool>()!=true,"Dimension is driven: "+wanted.Item1);Near(dim["value"]?.Value<double>(),wanted.Item2,1e-5,wanted.Item1);
                }
                var sketch=await Call("sketch_info",new JObject{{"sketch","MountingHole"}});
                var hole=sketch["segments"]!.Single(s=>(string?)s["kind"]=="circle");
                Near(hole["centerMm"]?[0]?.Value<double>(),20,1e-5,"hole sketch X");Near(hole["centerMm"]?[1]?.Value<double>(),10,1e-5,"hole sketch Y");
                var valid=await Call("model_check");Require(valid["valid"]?.Value<bool>()==true,"Native body check failed.");
                var features=await Call("feature_list");return features;
            }
            async Task Save(string file){
                string path=Path.Combine(output,file);await Call("doc_save",new JObject{{"mode","as"},{"path",path}});
                Require(File.Exists(path)&&new FileInfo(path).Length>1024,"Saved native part is missing or empty.");
                var header=new byte[8];using(var stream=File.OpenRead(path))Require(stream.Read(header,0,8)==8&&header.SequenceEqual(new byte[]{0xD0,0xCF,0x11,0xE0,0xA1,0xB1,0x1A,0xE1}),"Native OLE file header missing.");
            }
            var before=await Inspect(120);await Save("MountingPlate_120.sldprt");
            await Call("dimension_set",new JObject{{"values",new JArray(new JObject{{"name",width},{"value",150}})}});
            var after=await Inspect(150);
            Require(JToken.DeepEquals(before["features"],after["features"]),"Feature history changed during dimension edit.");
            await Save("MountingPlate_150.sldprt");
            var image=await Raw("view_screenshot",new JObject{{"view","iso"},{"maxSizePx",1200}});
            var png=(image["content"] as JArray)?.FirstOrDefault(b=>(string?)b["type"]=="image");
            if(png!=null)File.WriteAllBytes(Path.Combine(output,"MountingPlate.png"),Convert.FromBase64String(png["data"]!.ToString()));
            File.WriteAllText(Path.Combine(output,"acceptance.json"),new JObject{{"nativeVerified",true},{"documentHandle",doc},{"exercisedTools",new JArray(exercised.Distinct())},{"availableTools",session.Tools.Count},{"widthDimension",width},{"initialVolumeMm3",(120*60-Math.PI*16)*8},{"editedVolumeMm3",(150*60-Math.PI*16)*8}}.ToString(Formatting.Indented));
            // Leave the saved synthetic part open for inspection. No cleanup call follows an uncertain operation.
            Console.WriteLine("NATIVE ACCEPTANCE PASSED; both native parts and inspection evidence retained in "+output);return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
