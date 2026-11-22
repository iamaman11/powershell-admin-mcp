using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

// Existing DPAPI credential remains the primary profile. Optional extra files
// are independently encrypted, bounded, ACL-protected and versioned. No second
// store or daemon: the single interactive tunnel owner watches this directory.
internal static class TunnelProfiles
{
    internal const string Primary = "primary";
    internal const int PrimaryPort = 8080;
    internal const string DirectoryName = "tunnel-profiles";

    internal sealed record Profile(string Id, TunnelHost.Credential Credential, string Fingerprint)
    {
        internal int Port => Credential.HealthPort;
        internal string TunnelId => Credential.TunnelId;
    }

    internal static bool ValidName(string? id) => id is not null &&
        Regex.IsMatch(id, @"\A[a-z0-9][a-z0-9-]{0,31}\z", RegexOptions.CultureInvariant);

    internal static bool ValidSet(IEnumerable<Profile> profiles)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var tunnels = new HashSet<string>(StringComparer.Ordinal);
        var ports = new HashSet<int>();
        foreach (var profile in profiles)
        {
            if (!ValidName(profile.Id) || !TunnelHost.Valid(profile.Credential) ||
                !string.Equals(profile.Credential.ProfileId, profile.Id, StringComparison.Ordinal) ||
                (profile.Id == Primary) != (profile.Port == PrimaryPort) ||
                !names.Add(profile.Id) || !tunnels.Add(profile.TunnelId) || !ports.Add(profile.Port))
                return false;
        }
        return names.Contains(Primary);
    }

    internal static IReadOnlyList<Profile> Load(string dataRoot, SecurityIdentifier? owner)
    {
        TunnelHost.VerifyDirectory(dataRoot, owner);
        var primary = Path.Combine(dataRoot, "tunnel-credential.dpapi");
        var profiles = new List<Profile> { Read(primary, Primary, owner) };
        string directory = Path.Combine(dataRoot, DirectoryName);
        if (Directory.Exists(directory))
        {
            TunnelHost.VerifyDirectory(directory, owner);
            foreach (string path in Directory.EnumerateFiles(directory, "*.dpapi"))
            {
                string id = Path.GetFileNameWithoutExtension(path);
                if (!ValidName(id) || id == Primary)
                    throw new InvalidDataException("INVALID_TUNNEL_PROFILE_NAME");
                profiles.Add(Read(path, id, owner));
            }
        }
        if (!ValidSet(profiles)) throw new InvalidDataException("TUNNEL_PROFILE_CONFLICT");
        return profiles.OrderBy(p => p.Id == Primary ? "" : p.Id, StringComparer.Ordinal).ToArray();
    }

    private static Profile Read(string path, string id, SecurityIdentifier? owner)
    {
        TunnelHost.VerifyFile(path, owner);
        var size = new FileInfo(path).Length;
        if (size is < 1 or > 65536) throw new InvalidDataException("INVALID_TUNNEL_CREDENTIAL_SIZE");
        byte[] cipher = File.ReadAllBytes(path);
        string fingerprint = Convert.ToHexString(SHA256.HashData(cipher));
        byte[] plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.LocalMachine);
        try
        {
            var credential = JsonSerializer.Deserialize<TunnelHost.Credential>(plain);
            if (credential is null || !TunnelHost.Valid(credential) || credential.ProfileId != id)
                throw new InvalidDataException("INVALID_TUNNEL_CREDENTIAL");
            return new(id, credential, fingerprint);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(cipher);
        }
    }
}
