using Microsoft.Win32;

namespace ConnectionManager.Core.Caps;

/// A registry value as read. Data is a uint for REG_DWORD, a ulong for REG_QWORD, the unexpanded string for
/// REG_SZ and REG_EXPAND_SZ, a string[] for REG_MULTI_SZ, and the raw bytes for anything else.
public sealed record RegistryValue(RegistryValueKind Kind, object Data);

/// The few HKLM operations the caps need. Keys are paths under HKLM, such as SOFTWARE\Policies\...
public interface IRegistryStore
{
    /// Null when the value does not exist.
    RegistryValue? Read(string key, string name);

    /// Throws when the key does not exist.
    void WriteDword(string key, string name, uint data);

    void DeleteValue(string key, string name);

    bool KeyExists(string key);

    /// True when this call created the key, false when it already existed.
    bool CreateKey(string key);

    /// Deletes the key only when it has no values and no subkeys. True when it was deleted.
    bool DeleteKeyIfEmpty(string key);
}

/// The real store, in the 64-bit view. The service runs as SYSTEM, so it touches only the keys and values on a
/// hard-coded allowlist; anything else is a bug or a tampered journal and throws ArgumentException.
public sealed class HklmRegistryStore : IRegistryStore
{
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        [DoCapManager.PolicyKey] = [.. DoCapManager.ValueNames],
    };

    public RegistryValue? Read(string key, string name)
    {
        CheckValue(key, name);
        using var hklm = OpenHklm();
        using var opened = hklm.OpenSubKey(key);
        if (opened?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } data) return null;
        RegistryValueKind kind;
        try
        {
            kind = opened.GetValueKind(name);
        }
        catch (IOException)
        {
            return null; // deleted between the two reads
        }
        return new RegistryValue(kind, data switch
        {
            int dword when kind == RegistryValueKind.DWord => unchecked((uint)dword),
            long qword when kind == RegistryValueKind.QWord => unchecked((ulong)qword),
            _ => data,
        });
    }

    public void WriteDword(string key, string name, uint data)
    {
        CheckValue(key, name);
        using var hklm = OpenHklm();
        using var opened = hklm.OpenSubKey(key, writable: true) ?? throw new IOException($@"HKLM\{key} does not exist");
        opened.SetValue(name, unchecked((int)data), RegistryValueKind.DWord);
    }

    public void DeleteValue(string key, string name)
    {
        CheckValue(key, name);
        using var hklm = OpenHklm();
        using var opened = hklm.OpenSubKey(key, writable: true);
        opened?.DeleteValue(name, throwOnMissingValue: false);
    }

    public bool KeyExists(string key)
    {
        CheckKey(key);
        using var hklm = OpenHklm();
        using var opened = hklm.OpenSubKey(key);
        return opened is not null;
    }

    public bool CreateKey(string key)
    {
        if (KeyExists(key)) return false;
        using var hklm = OpenHklm();
        hklm.CreateSubKey(key, writable: false).Dispose(); // inherits the admin-only rules of its parent
        return true;
    }

    public bool DeleteKeyIfEmpty(string key)
    {
        CheckKey(key);
        using var hklm = OpenHklm();
        using (var opened = hklm.OpenSubKey(key))
        {
            if (opened is null || opened.ValueCount > 0 || opened.SubKeyCount > 0) return false;
        }
        hklm.DeleteSubKey(key, throwOnMissingSubKey: false);
        return true;
    }

    private static RegistryKey OpenHklm() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    private static void CheckKey(string key)
    {
        if (!Allowed.ContainsKey(key)) throw new ArgumentException($@"HKLM\{key} is not on the allowlist", nameof(key));
    }

    private static void CheckValue(string key, string name)
    {
        if (!Allowed.TryGetValue(key, out var names) || !names.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($@"HKLM\{key}\{name} is not on the allowlist", nameof(name));
    }
}
