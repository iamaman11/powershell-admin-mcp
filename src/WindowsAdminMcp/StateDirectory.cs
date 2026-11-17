using System.Security.AccessControl;
using System.Security.Principal;

internal static class StateDirectory
{
    internal static string DatabasePath()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindowsAdminMCP.Data");
        var directory = new DirectoryInfo(path);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(system);
        foreach (var sid in new[] { system, admins })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (!directory.Exists) directory.Create(acl);
        directory.Refresh();
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("State directory is a reparse point.");
        var current = directory.GetAccessControl();
        if (!current.AreAccessRulesProtected) throw new UnauthorizedAccessException("Inherited state directory ACL.");
        Verify(current);
        string db = Path.Combine(path,"jobs.db");
        foreach (string file in new[] {db, db+"-wal",db+"-shm",db+"-journal"})
        {
            if (!File.Exists(file)) continue;
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("State file is a reparse point.");
            Verify(info.GetAccessControl());
        }
        return db;

        void Verify(FileSystemSecurity security)
        {
            if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
                (!owner.Equals(system) && !owner.Equals(admins)))
                throw new UnauthorizedAccessException("Invalid state owner.");
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true,true,typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow &&
                    !rule.IdentityReference.Equals(system) && !rule.IdentityReference.Equals(admins))
                    throw new UnauthorizedAccessException("Unexpected identity in state ACL.");
        }
    }
}
