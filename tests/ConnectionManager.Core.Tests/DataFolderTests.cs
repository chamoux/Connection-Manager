using System.Security.AccessControl;
using System.Security.Principal;
using ConnectionManager.Core.Settings;

namespace ConnectionManager.Core.Tests;

/// Without admin rights only your own account can own a new folder, so these tests pass it in place of Administrators.
public sealed class DataFolderTests : IDisposable
{
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;

    private readonly TempTree _tree = new();
    private string Folder => Path.Combine(_tree.Root, "ConnectionManager");

    [Theory]
    [InlineData("")]
    [InlineData("logs")]
    public void Missing_folders_are_created_writable_only_by_admins_and_system(string sub)
    {
        Assert.Null(DataFolder.EnsureSafe(Folder, Me));

        var acl = new DirectoryInfo(Path.Combine(Folder, sub)).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        Assert.Equal(Me, acl.GetOwner(typeof(SecurityIdentifier)));
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        const FileSystemRights full = FileSystemRights.FullControl & ~FileSystemRights.Synchronize;
        Assert.Equal(Sorted([("S-1-5-18", full, all), (Me.Value, full, all), ("S-1-5-32-545", FileSystemRights.ReadAndExecute, all)]),
            Sorted(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Select(r => (r.IdentityReference.Value, r.FileSystemRights & ~FileSystemRights.Synchronize, r.InheritanceFlags))));
    }

    private static List<(string, FileSystemRights, InheritanceFlags)> Sorted(IEnumerable<(string Sid, FileSystemRights, InheritanceFlags)> rules) =>
        rules.OrderBy(r => r.Sid, StringComparer.Ordinal).ToList();

    [Fact]
    public void A_folder_it_created_passes_the_next_check() // the next start-up finds the folder already there
    {
        DataFolder.EnsureSafe(Folder, Me);

        Assert.Null(DataFolder.EnsureSafe(Folder, Me));
    }

    [Fact]
    public void A_folder_owned_by_a_normal_user_is_refused()
    {
        DataFolder.EnsureSafe(Folder, Me);

        Assert.Contains("install", DataFolder.EnsureSafe(Folder), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_folder_made_without_locking_it_down_is_refused() // what New-Item leaves: rules inherited from the parent
    {
        Directory.CreateDirectory(Folder);

        Assert.Contains("install", DataFolder.EnsureSafe(Folder, Me), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_locked_folder_that_lets_users_add_files_is_refused()
    {
        DataFolder.EnsureSafe(Folder, Me);
        var folder = new DirectoryInfo(Folder);
        var acl = folder.GetAccessControl(AccessControlSections.Access);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.CreateFiles, AccessControlType.Allow));
        folder.SetAccessControl(acl);

        Assert.Contains("install", DataFolder.EnsureSafe(Folder, Me), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_folder_that_links_elsewhere_is_refused()
    {
        var target = Directory.CreateDirectory(Path.Combine(_tree.Root, "elsewhere")).FullName;
        _tree.Junction(Folder, target);

        var problem = DataFolder.EnsureSafe(Folder, Me);

        Assert.Contains("link", problem);
        Assert.Contains("install", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_logs_folder_that_links_elsewhere_is_refused()
    {
        var target = Directory.CreateDirectory(Path.Combine(_tree.Root, "elsewhere")).FullName;
        DataFolder.EnsureSafe(Folder, Me);
        Directory.Delete(Path.Combine(Folder, "logs"));
        _tree.Junction(Path.Combine(Folder, "logs"), target);

        Assert.Contains("link", DataFolder.EnsureSafe(Folder, Me));
    }

    public void Dispose() => _tree.Dispose();
}
