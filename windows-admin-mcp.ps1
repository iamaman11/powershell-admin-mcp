# WindowsAdminMCP v0.3.0: single native stdio server, bounded async commands.
$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
Remove-Item Env:CONTROL_PLANE_API_KEY -ErrorAction SilentlyContinue
Remove-Item Env:OPENAI_API_KEY -ErrorAction SilentlyContinue
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Linq;

public static class WindowsAdminMcpServerV03 {
    static readonly object WriteLock = new object();
    static readonly object AuditLock = new object();
    static readonly SemaphoreSlim ExecGate = new SemaphoreSlim(1,1);
    static readonly List<Task> Pending = new List<Task>();
    static int Outstanding = 0;
    static string LogPath = "";
    static string User = "";
    static string PsVersion = "";
    static int Pid;
    static bool Elevated;
    static bool Enabled;
    const int MaxOutput = 16000;
    const int MaxInput = 131072;
    const int MaxQueue = 8;
    const string ToolsJson = @"{""tools"":[{""name"":""powershell_status"",""description"":""Read-only Windows host, PowerShell version and privilege check."",""inputSchema"":{""type"":""object"",""properties"":{},""additionalProperties"":false},""annotations"":{""readOnlyHint"":true,""destructiveHint"":false}},{""name"":""powershell_exec"",""description"":""Execute elevated PowerShell; serial execution with independent responsive status and ping, audit, bounded UTF-8 output and timeout."",""inputSchema"":{""type"":""object"",""properties"":{""command"":{""type"":""string"",""minLength"":1,""maxLength"":12000},""timeout_seconds"":{""type"":""integer"",""minimum"":1,""maximum"":120,""default"":30},""working_directory"":{""type"":""string""}},""required"":[""command""],""additionalProperties"":false},""annotations"":{""readOnlyHint"":false,""destructiveHint"":true,""idempotentHint"":false,""openWorldHint"":true}}]}";
    public static void Run(bool elevated,string user,string psVersion,int pid,string installDir) {
        Elevated=elevated;User=user;PsVersion=psVersion;Pid=pid;
        Enabled=String.Equals(Environment.GetEnvironmentVariable("WINDOWS_ADMIN_MCP_EXEC"),"1",StringComparison.Ordinal);
        LogPath=Path.Combine(installDir,"logs","commands.jsonl");
        while(true){
            var line=ReadBoundedLine();
            if(line==null)break;
            if(line.Length>MaxInput){Error("null",-32600,"Request too large");continue;}
            try{Handle(line);}catch(Exception){Error("null",-32603,"Internal error");}
        }
        Task[] tasks;
        lock(Pending){tasks=Pending.ToArray();}
        try{Task.WaitAll(tasks,TimeSpan.FromSeconds(140));}catch{}
    }
    static string ReadBoundedLine(){
        var sb=new StringBuilder();
        bool over=false;
        while(true){
            int c=Console.In.Read();
            if(c<0){if(sb.Length==0 && !over)return null;break;}
            if(c==10)break;
            if(c==13)continue;
            if(sb.Length<=MaxInput)sb.Append((char)c);
            else over=true;
        }
        return over ? new string(' ',MaxInput+1) : sb.ToString();
    }
    static void Send(string s){lock(WriteLock){Console.Out.WriteLine(s);Console.Out.Flush();}}
    static void Result(string id,string resultJson){Send("{\"jsonrpc\":\"2.0\",\"id\":"+id+",\"result\":"+resultJson+"}");}
    static void Error(string id,int code,string msg){Send("{\"jsonrpc\":\"2.0\",\"id\":"+id+",\"error\":{\"code\":"+code+",\"message\":"+JsonSerializer.Serialize(msg)+"}}");}
    static bool Property(JsonElement root,string name,out JsonElement value){if(root.ValueKind==JsonValueKind.Object)return root.TryGetProperty(name,out value);value=default;return false;}
    static void Handle(string raw){
        JsonDocument doc;
        try{doc=JsonDocument.Parse(raw);}catch(JsonException){Error("null",-32700,"Parse error");return;}
        using(doc){
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object){Error("null",-32600,"Invalid Request");return;}
            bool hasId=Property(root,"id",out var id);
            string idJson=hasId?id.GetRawText():"null";
            if(hasId && id.ValueKind!=JsonValueKind.String && id.ValueKind!=JsonValueKind.Number && id.ValueKind!=JsonValueKind.Null){Error("null",-32600,"Invalid id");return;}
            if(!Property(root,"jsonrpc",out var protocol)||protocol.ValueKind!=JsonValueKind.String||protocol.GetString()!="2.0"||!Property(root,"method",out var method)||method.ValueKind!=JsonValueKind.String){Error(idJson,-32600,"Invalid Request");return;}
            if(!hasId)return; // JSON-RPC notification, no response.
            switch(method.GetString()){
                case "initialize":
                    Result(idJson,"{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"windows-admin-mcp\",\"version\":\"0.3.0\"}}");break;
                case "ping":Result(idJson,"{}");break;
                case "tools/list":Result(idJson,ToolsJson);break;
                case "resources/list":Result(idJson,"{\"resources\":[]}");break;
                case "prompts/list":Result(idJson,"{\"prompts\":[]}");break;
                case "tools/call":CallTool(root,idJson);break;
                default:Error(idJson,-32601,"Method not found");break;
            }
        }
    }
    static void CallTool(JsonElement root,string id){
        if(!Property(root,"params",out var param)||param.ValueKind!=JsonValueKind.Object||!Property(param,"name",out var n)||n.ValueKind!=JsonValueKind.String||String.IsNullOrWhiteSpace(n.GetString())){Error(id,-32602,"Invalid params");return;}
        string name=n.GetString();
        if(name!="powershell_exec"&&name!="powershell_status"){Error(id,-32602,"Unknown tool");return;}
        JsonElement args=default;
        if(Property(param,"arguments",out args)&&args.ValueKind!=JsonValueKind.Object){Error(id,-32602,"Invalid arguments");return;}
        if(name=="powershell_status"){
            if(args.ValueKind==JsonValueKind.Object && args.EnumerateObject().Any()){Error(id,-32602,"Unexpected arguments");return;}
            ToolResult(id,Status(),false);return;
        }
        string command=null,workingDir=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        int seconds=30;
        if(args.ValueKind!=JsonValueKind.Object){Error(id,-32602,"Missing arguments");return;}
        foreach(var p in args.EnumerateObject()){
            if(p.Name=="command"){
                if(p.Value.ValueKind!=JsonValueKind.String){Error(id,-32602,"Invalid command");return;}
                command=p.Value.GetString();
            }else if(p.Name=="timeout_seconds"){
                if(p.Value.ValueKind!=JsonValueKind.Number||!p.Value.TryGetInt32(out seconds)||seconds<1||seconds>120){Error(id,-32602,"Invalid timeout");return;}
            }else if(p.Name=="working_directory"){
                if(p.Value.ValueKind!=JsonValueKind.String){Error(id,-32602,"Invalid directory");return;}
                workingDir=p.Value.GetString();
            }else{Error(id,-32602,"Unknown argument");return;}
        }
        if(String.IsNullOrWhiteSpace(command)||command.Length>12000){Error(id,-32602,"Invalid command length");return;}
        if(!Enabled){ToolResult(id,new Dictionary<string,object>{{"status","BLOCKED"},{"reason","ADMIN_EXEC_NOT_ENABLED_LOCALLY"}},true);return;}
        if(!Elevated){ToolResult(id,new Dictionary<string,object>{{"status","BLOCKED"},{"reason","NOT_ELEVATED"}},true);return;}
        if(!Directory.Exists(workingDir)){ToolResult(id,new Dictionary<string,object>{{"status","BLOCKED"},{"reason","WORKING_DIRECTORY_NOT_FOUND"}},true);return;}
        if(Interlocked.Increment(ref Outstanding)>MaxQueue){Interlocked.Decrement(ref Outstanding);ToolResult(id,new Dictionary<string,object>{{"status","BLOCKED"},{"reason","EXEC_QUEUE_FULL"}},true);return;}
        Task job=Task.Run(async()=>{
            try{
                await ExecGate.WaitAsync().ConfigureAwait(false);
                try{
                    var result=await Execute(command,seconds,workingDir).ConfigureAwait(false);
                    bool fail=!String.Equals((string)result["status"],"COMPLETED",StringComparison.Ordinal) || !(result.TryGetValue("exit_code",out var exit) && exit is int code && code==0);
                    ToolResult(id,result,fail);
                }finally{ExecGate.Release();}
            }catch(Exception){ToolResult(id,new Dictionary<string,object>{{"status","EXECUTION_FAILED"},{"reason","INTERNAL_EXECUTION_ERROR"}},true);}
            finally{Interlocked.Decrement(ref Outstanding);}
        });
        lock(Pending){Pending.Add(job);Pending.RemoveAll(t=>t.IsCompleted);}
    }
    static Dictionary<string,object> Status(){
        return new Dictionary<string,object>{
            {"host",Environment.MachineName},{"user",User},{"elevated",Elevated},{"powershell",PsVersion},{"process_id",Pid},{"timestamp_utc",DateTime.UtcNow.ToString("O")}
        };
    }
    static void ToolResult(string id,Dictionary<string,object> data,bool fail){
        string content=JsonSerializer.Serialize(data);
        string result="{\"content\":[{\"type\":\"text\",\"text\":"+JsonSerializer.Serialize(content)+"}],\"structuredContent\":"+content+",\"isError\":"+(fail?"true":"false")+"}";
        Result(id,result);
    }
    static void Audit(Dictionary<string,object> data){
        lock(AuditLock){
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            if(File.Exists(LogPath)&&new FileInfo(LogPath).Length>=8*1024*1024){
                File.Move(LogPath,Path.Combine(Path.GetDirectoryName(LogPath),"commands.previous.jsonl"),true);
            }
            data["timestamp_utc"]=DateTime.UtcNow.ToString("O");
            File.AppendAllText(LogPath,JsonSerializer.Serialize(data)+"\n",new UTF8Encoding(false));
        }
    }
    static async Task<Dictionary<string,object>> Execute(string cmd,int timeout,string cwd){
        string corr=Guid.NewGuid().ToString("N");
        string hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cmd))).ToLowerInvariant();
        try{Audit(new Dictionary<string,object>{{"event","admit"},{"correlation_id",corr},{"command_sha256",hash},{"host",Environment.MachineName},{"user",User},{"timeout_seconds",timeout}});}
        catch{return new Dictionary<string,object>{{"status","BLOCKED"},{"reason","AUDIT_LOG_UNAVAILABLE"},{"command_sha256",hash}};}
        var clock=Stopwatch.StartNew();
        var result=new Dictionary<string,object>{
            {"status","EXECUTION_FAILED"},{"reason","PROCESS_OR_CAPTURE_FAILURE"},{"command_sha256",hash},{"correlation_id",corr},
            {"host",Environment.MachineName},{"user",User},{"elevated",Elevated}
        };
        try{
            string wrapper="$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';"+
                "[Console]::InputEncoding=[Text.UTF8Encoding]::new($false);[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);"+
                "$OutputEncoding=[Text.UTF8Encoding]::new($false);$global:Error.Clear();"+
                "try { & { "+cmd+" }; if ($global:Error.Count -gt 0) {[Console]::Error.WriteLine('POWERSHELL_ERROR_RECORDED'); exit 1} }"+
                " catch { [Console]::Error.WriteLine($_.ToString()); exit 1 }";
            var psi=new ProcessStartInfo(Path.Combine(Environment.GetEnvironmentVariable("PSHOME")??Path.GetDirectoryName(Environment.ProcessPath),"pwsh.exe")){
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
                StandardOutputEncoding=new UTF8Encoding(false),StandardErrorEncoding=new UTF8Encoding(false),WorkingDirectory=cwd
            };
            psi.Environment.Remove("CONTROL_PLANE_API_KEY");
            psi.Environment.Remove("OPENAI_API_KEY");
            psi.ArgumentList.Add("-NoLogo");psi.ArgumentList.Add("-NoProfile");psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapper)));
            using(var process=new Process{StartInfo=psi}){
                if(!process.Start())throw new InvalidOperationException();
                var output=Drain(process.StandardOutput,MaxOutput);
                var errors=Drain(process.StandardError,MaxOutput);
                bool timedOut=false;
                using(var cts=new CancellationTokenSource(TimeSpan.FromSeconds(timeout))){
                    try{await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);}
                    catch(OperationCanceledException){
                        timedOut=true;
                        try{process.Kill(true);}catch{}
                        using(var grace=new CancellationTokenSource(TimeSpan.FromSeconds(5))){
                            try{await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);}catch{}
                        }
                    }
                }
                var capture=await Task.WhenAll(output,errors).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var stderr=CleanStderr(capture[1].Text);
                bool errorFound=!String.IsNullOrWhiteSpace(stderr);
                int? code=timedOut||!process.HasExited?(int?)null:process.ExitCode;
                string state=timedOut?"TIMEOUT_UNKNOWN_EFFECT":(!code.HasValue?"OUTPUT_INCOMPLETE":(code.Value!=0||errorFound?"COMMAND_ERROR":"COMPLETED"));
                result["status"]=state;result.Remove("reason");
                result["exit_code"]=code;result["stdout"]=capture[0].Text;result["stderr"]=stderr;
                result["output_truncated"]=capture[0].Truncated||capture[1].Truncated;
            }
        }catch(Exception){result["status"]="EXECUTION_FAILED";result["reason"]="PROCESS_OR_CAPTURE_FAILURE";}
        finally{clock.Stop();}
        result["elapsed_ms"]=clock.ElapsedMilliseconds;
        try{
            Audit(new Dictionary<string,object>{{"event","finish"},{"correlation_id",corr},{"command_sha256",hash},
                {"status",result["status"]},{"exit_code",result.ContainsKey("exit_code")?result["exit_code"]:null},
                {"elapsed_ms",clock.ElapsedMilliseconds},{"output_truncated",result.ContainsKey("output_truncated")?result["output_truncated"]:false}});
            result["audit_persisted"]=true;
        }catch{result["audit_persisted"]=false;result["status"]="AUDIT_WRITE_FAILED_AFTER_EXECUTION";}
        return result;
    }
    sealed class Captured {public string Text="";public bool Truncated;}
    static async Task<Captured> Drain(StreamReader reader,int limit){
        var sb=new StringBuilder(Math.Min(limit,4096));
        var buf=new char[4096];bool truncated=false;int n;
        while((n=await reader.ReadAsync(buf,0,buf.Length).ConfigureAwait(false))>0){
            int keep=Math.Min(Math.Max(0,limit-sb.Length),n);
            if(keep>0)sb.Append(buf,0,keep);
            if(keep<n)truncated=true;
        }
        return new Captured{Text=sb.ToString(),Truncated=truncated};
    }
    static string CleanStderr(string value){
        if(!value.TrimStart().StartsWith("#< CLIXML",StringComparison.Ordinal))return value;
        var items=Regex.Matches(value,"<S\\s+S=\"Error\"[^>]*>(.*?)</S>",RegexOptions.Singleline);
        if(items.Count==0)return "";
        var sb=new StringBuilder();
        foreach(Match m in items){
            string s=WebUtility.HtmlDecode(m.Groups[1].Value);
            s=Regex.Replace(s,"_x([0-9A-Fa-f]{4})_",(Match x)=>((char)Convert.ToInt32(x.Groups[1].Value,16)).ToString());
            s=Regex.Replace(s,"\u001b\\[[0-9;]*m","");
            sb.AppendLine(s);
        }
        return sb.ToString();
    }
}
'@ -ErrorAction Stop
[WindowsAdminMcpServerV03]::Run($elevated,$identity.Name,$PSVersionTable.PSVersion.ToString(),$PID,$PSScriptRoot)
