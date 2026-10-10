using System.Security.AccessControl;
using System.Security.Principal;

namespace ConnectionManager.Core.Settings;

/// Any user can create folders in ProgramData, so the data folder is checked before the app reads settings from it
/// or writes logs to it: a folder planted by a normal user, one that links elsewhere, or one that normal users can
/// add files to, is refused. It is never repaired in place: that would keep whatever a user already put inside.
public static class DataFolder
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    // Adding, changing or deleting anything inside, or changing who may. Inherited entries can hold the generic
    // GENERIC_ALL and GENERIC_WRITE bits instead of the specific ones.
    private const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData
        | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership
        | (FileSystemRights)0x50000000;

    /// Null when the folder is safe to use, otherwise the problem in plain English. A missing folder, or logs folder,
    /// is created with the rules install.ps1 sets: admins and SYSTEM may write, users may only read.
    public static string? EnsureSafe(string folder) => EnsureSafe(folder, Administrators);

    // Only an admin can make Administrators the owner; tests without admin rights pass their own account, which then
    // stands in for Administrators everywhere.
    internal static string? EnsureSafe(string folder, SecurityIdentifier admins)
    {
        try
        {
            // Each folder is checked even after creating it: someone may have made it first, which CreateDirectory accepts.
            if (!Directory.Exists(folder)) Locked(admins).CreateDirectory(folder);
            if (Problem(folder, admins, mustBeLocked: true) is { } problem) return Untrusted(problem);

            // Created here with an explicit owner: under the "object creator" owner policy, FileLog would give it to the user.
            // An existing one only needs safe rules; one FileLog made inherits them from the data folder.
            var logs = Path.Combine(folder, "logs");
            if (!Path.Exists(logs)) Locked(admins).CreateDirectory(logs);
            return Problem(logs, admins, mustBeLocked: false) is { } logsProblem ? Untrusted(logsProblem) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Connection Manager could not check {folder}: {ex.Message}";
        }
    }

    private static string? Problem(string path, SecurityIdentifier admins, bool mustBeLocked)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return $"{path} is a link to somewhere else";
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        bool Trusted(IdentityReference? sid) => admins.Equals(sid) || LocalSystem.Equals(sid);

        if (!Trusted(security.GetOwner(typeof(SecurityIdentifier)))) return $"{path} was not created by an admin";
        var userCanWrite = (mustBeLocked && !security.AreAccessRulesProtected) // inherits ProgramData's "users may add files"
            || security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Any(r => r.AccessControlType == AccessControlType.Allow && (r.FileSystemRights & WriteRights) != 0 && !Trusted(r.IdentityReference));
        return userCanWrite ? $"{path} can be changed by users who are not admins" : null;
    }

    private static string Untrusted(string why) =>
        $"{why}, so Connection Manager will not use it. Run install.cmd again to fix it (settings start fresh if the folder cannot be trusted).";

    private static DirectorySecurity Locked(SecurityIdentifier admins)
    {
        const InheritanceFlags everything = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetOwner(admins);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, everything, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, everything, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, everything, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}
