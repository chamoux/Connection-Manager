# Shared by install.ps1, uninstall.ps1 and tools\smoke.ps1, which dot-source it. It only defines names and functions.
# The folder rules are the service's (DataFolder.EnsureSafe in ConnectionManager.Core): keep the two in step.

$AdminsSid = "S-1-5-32-544"
$SystemSid = "S-1-5-18"
# The account this elevated run uses. Under the "object creator" owner policy, what an admin's elevated run makes is the
# admin's own, not the Administrators group's.
$MySid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
# The data folder's whole security descriptor: Administrators own it, admins and SYSTEM may write, users may only read,
# and nothing is inherited from ProgramData (where users may add files).
$DataFolderSddl = "O:BAD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;OICI;0x1200a9;;;BU)"
# Adding, changing or deleting anything inside, or changing who may. Inherited entries can hold the generic GENERIC_ALL
# and GENERIC_WRITE bits instead of the specific ones.
$WriteRights = 0x500D0156

# A machine folder from its admin-only registry value, never from the environment: a user can override %ProgramData%
# (and the known folders built on it, such as the Start menu's), and the override reaches an elevated run. Only
# %SystemDrive% (the drive of the system folder, from GetSystemDirectory, not the environment) and the names in $Expand
# are expanded. A value with anything else left unexpanded is refused.
function Get-MachineFolder([string]$Key, [string]$Name, [hashtable]$Expand = @{}) {
    $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    $subKey = $null
    try {
        $subKey = $machine.OpenSubKey($Key)
        $raw = if ($subKey) { $subKey.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
    }
    finally {
        if ($subKey) { $subKey.Close() }
        $machine.Close()
    }
    $path = "$raw"
    $names = @{ SystemDrive = [IO.Path]::GetPathRoot([Environment]::SystemDirectory).TrimEnd("\") } + $Expand
    foreach ($entry in $names.GetEnumerator()) { $path = $path -ireplace [regex]::Escape("%$($entry.Key)%"), $entry.Value.Replace('$', '$$') }
    if ($path -notmatch '^[A-Za-z]:\\[^%]*$') { throw "HKLM\$Key\$Name is '$raw', which is not a local folder this script can use." }
    $path.TrimEnd("\")
}

# Null when $Path is a folder the service accepts, otherwise the problem: the rules of DataFolder.Problem. $Owners may own
# it, nobody else may have a write-type right, and a data folder ($MustBeLocked) must not inherit. The link check comes
# first, because Get-Acl reads what a link points to.
function Get-FolderProblem([string]$Path, [switch]$MustBeLocked, [string[]]$Owners = @($AdminsSid, $SystemSid)) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { return "$Path is a link to somewhere else" }
    if (-not $item.PSIsContainer) { return "$Path is not a folder" }
    $acl = Get-Acl -LiteralPath $Path
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $Owners) { return "$Path was not created by an admin" }
    $writers = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object {
        $_.AccessControlType -eq "Allow" -and ([int]$_.FileSystemRights -band $WriteRights) -and $_.IdentityReference.Value -notin $AdminsSid, $SystemSid })
    if (($MustBeLocked -and -not $acl.AreAccessRulesProtected) -or $writers) { return "$Path can be changed by users who are not admins" }
    $null
}

# Null when the data folder can be kept, otherwise why not. The folder and its logs folder must pass the service's rules,
# and nothing directly in either may be a link or belong to anyone but Administrators or SYSTEM. With -AllowMine, what
# you own (this elevated run's account) also passes: only admins can add files to a folder that passed those rules, so
# yours were made by an elevated run, such as uninstall's restore. Anything that cannot be read is a problem.
function Get-DataFolderProblem([string]$Folder, [switch]$AllowMine) {
    $owners = @($AdminsSid, $SystemSid)
    if ($AllowMine) { $owners += $MySid }
    try {
        $problem = Get-FolderProblem $Folder -MustBeLocked
        if ($problem) { return $problem }
        $items = @(Get-ChildItem -LiteralPath $Folder -Force)
        $logs = Join-Path $Folder "logs"
        if (Test-Path -LiteralPath $logs) {
            $problem = Get-FolderProblem $logs -Owners $owners
            if ($problem) { return $problem }
            $items += @(Get-ChildItem -LiteralPath $logs -Force)
        }
        foreach ($item in $items) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { return "$($item.FullName) is a link to somewhere else" }
            if ((Get-Acl -LiteralPath $item.FullName).GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $owners) { return "$($item.FullName) was not created by an admin" }
        }
    }
    catch { return "$Folder could not be checked: $($_.Exception.Message)" }
    $null
}

# Makes Administrators the owner of what you own in the data folder, one item at a time: its logs folder and the files
# directly in each. Never icacls /T, which walks into a junction and changes owners outside the folder; a link is skipped
# with a warning. Call it only on a folder Get-DataFolderProblem -AllowMine accepts: only admins can change what is
# inside, so nothing can be swapped for a link while this runs.
function Set-AdminOwner([string]$Folder) {
    $admins = New-Object Security.Principal.SecurityIdentifier $AdminsSid
    $items = @(Get-ChildItem -LiteralPath $Folder -Force)
    $logs = Join-Path $Folder "logs"
    if ((Test-Path -LiteralPath $logs -PathType Container) -and -not ((Get-Item -LiteralPath $logs -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        $items += @(Get-ChildItem -LiteralPath $logs -Force)
    }
    foreach ($item in $items) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            Write-Warning "$($item.FullName) is a link to somewhere else, so its owner was left as it is."
            continue
        }
        $acl = Get-Acl -LiteralPath $item.FullName
        if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $MySid) { continue }
        $acl.SetOwner($admins)
        Set-Acl -LiteralPath $item.FullName -AclObject $acl
    }
}

# Creates the folder $Path with the whole security descriptor $Sddl in one step: CreateDirectory itself sets the owner and
# the protected DACL, so the folder is never someone else's and users can never add to it. CreateDirectory also accepts a
# folder or link that is already there, so the result is checked: only an admin can make Administrators the owner, so a
# folder that passes is the one made here.
function New-LockedFolder([string]$Path, [string]$Sddl) {
    $taken = "$Path was created by someone else while install ran. Run install again."
    if (Test-Path -LiteralPath $Path) { throw $taken }
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetSecurityDescriptorSddlForm($Sddl)
    if ($PSVersionTable.PSVersion.Major -le 5) { [void][IO.Directory]::CreateDirectory($Path, $security) }
    else { [void][IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($Path), $security) }
    $problem = Get-FolderProblem $Path -MustBeLocked
    if ($problem -or (Get-ChildItem -LiteralPath $Path -Force)) { throw $taken }
}

# Copies the folder $From into $To without junctions or symbolic links (robocopy /XJ). Copy-Item -Recurse would copy what
# a link points to, and this runs elevated. robocopy's exit codes below 8 mean success.
function Copy-Tree([string]$From, [string]$To) {
    $robocopy = Join-Path ([Environment]::GetFolderPath("System")) "robocopy.exe"
    $ErrorActionPreference = "Continue"
    $output = @(& $robocopy $From $To /E /XJ /R:1 /W:1 /NP /NFL /NDL /NJH 2>&1 | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
    if ($LASTEXITCODE -ge 8) { throw "robocopy could not copy $From to $To (exit $LASTEXITCODE): $($output -join ' ')" }
}
