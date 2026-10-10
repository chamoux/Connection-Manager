using ConnectionManager.Core.Caps;
using Microsoft.Win32;

namespace ConnectionManager.Core.Tests;

/// Keys and values in memory. Unlike the real store it accepts any key or value name, so the tests
/// prove DoCapManager keeps to its own allowlist.
internal sealed class FakeRegistryStore : IRegistryStore
{
    private int _writes;

    public Dictionary<string, Dictionary<string, RegistryValue>> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// How many DWORD writes succeed before the next one throws.
    public int FailWritesAfter { get; set; } = int.MaxValue;
    public bool FailDeletes { get; set; }

    /// Sets a value as some other program would, creating the key.
    public void Set(string key, string name, RegistryValue value)
    {
        CreateKey(key);
        Keys[key][name] = value;
    }

    public RegistryValue? Read(string key, string name) =>
        Keys.TryGetValue(key, out var values) && values.TryGetValue(name, out var value) ? value : null;

    public void WriteDword(string key, string name, uint data)
    {
        if (_writes++ >= FailWritesAfter) throw new IOException("write failed");
        if (!Keys.TryGetValue(key, out var values)) throw new IOException($"{key} does not exist");
        values[name] = new RegistryValue(RegistryValueKind.DWord, data);
    }

    public void DeleteValue(string key, string name)
    {
        if (FailDeletes) throw new UnauthorizedAccessException("delete failed");
        if (Keys.TryGetValue(key, out var values)) values.Remove(name);
    }

    public bool KeyExists(string key) => Keys.ContainsKey(key);

    public bool CreateKey(string key) => Keys.TryAdd(key, new Dictionary<string, RegistryValue>(StringComparer.OrdinalIgnoreCase));

    public bool DeleteKeyIfEmpty(string key)
    {
        if (FailDeletes) throw new UnauthorizedAccessException("delete failed");
        return Keys.TryGetValue(key, out var values) && values.Count == 0 && Keys.Remove(key);
    }
}
