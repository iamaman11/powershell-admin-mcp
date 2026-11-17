using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<PackageCode>))]
internal enum PackageCode { VERIFIED, MANIFEST_MISSING, INVALID_MANIFEST, UNSAFE_PATH_OR_ACL,
    FILE_SET_MISMATCH, HASH_MISMATCH, READBACK_FAILED }
internal sealed record PackageObservation(PackageCode Status,string? Commit=null,int? FileCount=null,string? File=null);
internal static class PackageIntegrity
{
    private static readonly SecurityIdentifier Admins=new(WellKnownSidType.BuiltinAdministratorsSid,null);
    private static readonly SecurityIdentifier SystemSid=new(WellKnownSidType.LocalSystemSid,null);
    private static bool Protected(FileSystemInfo item)
    {
        if((item.Attributes&FileAttributes.ReparsePoint)!=0)return false;
        FileSystemSecurity acl=item switch
        {
            DirectoryInfo d=>d.GetAccessControl(),
            FileInfo f=>f.GetAccessControl(),
            _=>throw new InvalidOperationException()
        };
        if(acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            (!owner.Equals(Admins)&&!owner.Equals(SystemSid)))return false;
        return !acl.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Any(r=>r.AccessControlType==AccessControlType.Allow && !r.IdentityReference.Equals(Admins) && !r.IdentityReference.Equals(SystemSid));
    }
    // The manifest is relative to a protected package root. Never follow a
    // directory reparse point, and never hash a file before checking its parents.
    internal static PackageObservation Observe(string root)
    {
        try
        {
            var directory=new DirectoryInfo(root);
            if(!directory.Exists)return new(PackageCode.MANIFEST_MISSING);
            if(!Protected(directory))return new(PackageCode.UNSAFE_PATH_OR_ACL);
            var manifest=new FileInfo(Path.Combine(root,"manifest.json"));
            if(!manifest.Exists)return new(PackageCode.MANIFEST_MISSING);
            if(!Protected(manifest))return new(PackageCode.UNSAFE_PATH_OR_ACL);
            if(manifest.Length is <=0 or >524288)return new(PackageCode.INVALID_MANIFEST);
            using var json=JsonDocument.Parse(File.ReadAllBytes(manifest.FullName));
            var m=json.RootElement;
            if(m.ValueKind!=JsonValueKind.Object ||
                !m.TryGetProperty("schema",out var schema) || schema.ValueKind!=JsonValueKind.Number || !schema.TryGetInt32(out int v) || v!=1 ||
                !m.TryGetProperty("commit",out var commitEl) || commitEl.ValueKind!=JsonValueKind.String ||
                !m.TryGetProperty("vendor_version",out var version) || version.ValueKind!=JsonValueKind.String || version.GetString()!="0.0.21" ||
                !m.TryGetProperty("files",out var names) || names.ValueKind!=JsonValueKind.Object)
                return new(PackageCode.INVALID_MANIFEST);
            string? commit=commitEl.GetString();
            if(commit is null || !Regex.IsMatch(commit,@"\A[a-f0-9]{40}\z"))return new(PackageCode.INVALID_MANIFEST);
            var expected=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(var e in names.EnumerateObject())
            {
                if(expected.Count>=2000 || !Regex.IsMatch(e.Name,@"\A(native|client)/[A-Za-z0-9_./+-]+\z") ||
                    e.Name.Split('/').Any(p=>p.Length==0||p=="."||p==".."||p.EndsWith('.')) ||
                    e.Value.ValueKind!=JsonValueKind.String || e.Value.GetString() is not {} hash ||
                    !Regex.IsMatch(hash,@"\A[A-F0-9]{64}\z") || !expected.TryAdd(e.Name,hash))
                    return new(PackageCode.INVALID_MANIFEST,commit);
            }
            if(!expected.ContainsKey("native/WindowsAdminMCP.exe") ||
                !expected.ContainsKey("native/NativeStartup.ps1") ||
                !expected.TryGetValue("client/tunnel-client-runtime.exe",out var vendor) || vendor!=TunnelHost.VendorSha256)
                return new(PackageCode.INVALID_MANIFEST,commit);
            var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending=new Stack<DirectoryInfo>();
            foreach(string sub in new[]{"native","client"})
            {
                var d=new DirectoryInfo(Path.Combine(root,sub));
                if(!d.Exists)return new(PackageCode.FILE_SET_MISMATCH,commit,File:sub);
                pending.Push(d);
            }
            int items=0;long bytes=0;
            while(pending.TryPop(out var d))
            {
                if(!Protected(d))return new(PackageCode.UNSAFE_PATH_OR_ACL,commit);
                foreach(var item in d.EnumerateFileSystemInfos())
                {
                    if(++items>2200)return new(PackageCode.FILE_SET_MISMATCH,commit);
                    if(!Protected(item))return new(PackageCode.UNSAFE_PATH_OR_ACL,commit);
                    if(item is DirectoryInfo sub){pending.Push(sub);continue;}
                    if(item is not FileInfo f)return new(PackageCode.FILE_SET_MISMATCH,commit);
                    string rel=Path.GetRelativePath(root,f.FullName).Replace('\\','/');
                    if(!expected.TryGetValue(rel,out var hash)||!visited.Add(rel))return new(PackageCode.FILE_SET_MISMATCH,commit,File:rel);
                    bytes+=f.Length;
                    if(bytes>536870912)return new(PackageCode.FILE_SET_MISMATCH,commit);
                    using var stream=File.OpenRead(f.FullName);
                    if(Convert.ToHexString(SHA256.HashData(stream))!=hash)return new(PackageCode.HASH_MISMATCH,commit,File:rel);
                }
            }
            return visited.Count==expected.Count?new(PackageCode.VERIFIED,commit,visited.Count):
                new(PackageCode.FILE_SET_MISMATCH,commit,visited.Count);
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException or InvalidOperationException or ArgumentException)
        {return new(PackageCode.READBACK_FAILED);}
    }
}
