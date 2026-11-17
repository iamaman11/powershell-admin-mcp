using System.Security.Cryptography;
using System.Text;

// The Windows service runs as LocalSystem. CurrentUser DPAPI binds job data to
// that Windows identity on this installation; filesystem ACL is still mandatory.
internal static class JobDataProtection
{
    private static byte[] Entropy(string id,string field)
        => Encoding.UTF8.GetBytes("WindowsAdminMCP/jobs/v1/"+id+"/"+field);
    internal static byte[] Protect(string value,string id,string field)
    {
        byte[] plaintext=Encoding.UTF8.GetBytes(value);
        try { return ProtectedData.Protect(plaintext,Entropy(id,field),DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    internal static string Open(byte[] value,string id,string field)
    {
        byte[] plaintext=ProtectedData.Unprotect(value,Entropy(id,field),DataProtectionScope.CurrentUser);
        try { return new UTF8Encoding(false,true).GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}
