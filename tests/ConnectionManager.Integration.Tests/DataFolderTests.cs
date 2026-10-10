using System.Security.AccessControl;
using System.Security.Principal;
using ConnectionManager.Core.Settings;

namespace ConnectionManager.Integration.Tests;

/// Only an admin can make Administrators the owner, so the real rules are checked here.
public sealed class DataFolderTests : IDisposable
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private readonly string _root = Directory.CreateTempSubdirectory("cm-data-").FullName;
    private string Folder => Path.Combine(_root, "ConnectionManager");

    [ElevatedFact]
    public void A_new_folder_is_owned_by_administrators_with_inheritance_off()
    {
        Assert.Null(DataFolder.EnsureSafe(Folder));

        var acl = new DirectoryInfo(Folder).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        Assert.Equal(Administrators, acl.GetOwner(typeof(SecurityIdentifier)));
        Assert.Null(DataFolder.EnsureSafe(Folder)); // and the next start-up accepts it
    }

    [ElevatedFact]
    public void The_logs_folder_it_creates_is_owned_by_administrators() // even under the "object creator" owner policy
    {
        Assert.Null(DataFolder.EnsureSafe(Folder));

        var acl = new DirectoryInfo(Path.Combine(Folder, "logs")).GetAccessControl();
        Assert.Equal(Administrators, acl.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(acl.AreAccessRulesProtected);
    }

    [ElevatedFact]
    public void A_logs_folder_owned_by_a_normal_user_is_refused()
    {
        DataFolder.EnsureSafe(Folder);
        var logs = Directory.CreateDirectory(Path.Combine(Folder, "logs"));
        var acl = logs.GetAccessControl();
        acl.SetOwner(WindowsIdentity.GetCurrent().User!); // your own account, not the Administrators group
        logs.SetAccessControl(acl);

        Assert.Contains("install", DataFolder.EnsureSafe(Folder), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
