using Revenant_Theme_Studio.Features.Themes.Platform;

namespace RTS.Tests.Fakes;

/// <summary>An in-memory registry with the same rules as the real store.</summary>
public sealed class FakeRegistryStore : IRegistryStore
{
    private readonly Dictionary<string, Dictionary<string, RegData>> _keys = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Writes { get; } = new();

    private static string Id(RegHive hive, string key) => $"{hive}\\{key.Trim('\\')}";

    public bool KeyExists(RegHive hive, string key) => _keys.ContainsKey(Id(hive, key));

    public RegData? Get(RegHive hive, string key, string name) =>
        _keys.TryGetValue(Id(hive, key), out var values) && values.TryGetValue(name, out var d) ? d : null;

    public IReadOnlyList<string> ValueNames(RegHive hive, string key) =>
        _keys.TryGetValue(Id(hive, key), out var values) ? values.Keys.ToList() : new List<string>();

    public void Set(RegHive hive, string key, string name, RegData data)
    {
        if (hive != RegHive.CurrentUser) throw new InvalidOperationException("HKCU only");
        if (data.Unreadable) throw new InvalidOperationException("unreadable kind");
        // Create the key and every missing parent, like RegistryKey.CreateSubKey.
        var parts = key.Trim('\\').Split('\\');
        for (int i = 1; i <= parts.Length; i++)
        {
            string id = Id(hive, string.Join('\\', parts.Take(i)));
            if (!_keys.ContainsKey(id)) _keys[id] = new(StringComparer.OrdinalIgnoreCase);
        }
        _keys[Id(hive, key)][name] = data;
        Writes.Add($"{key}\\{name}={data}");
    }

    public void DeleteValue(RegHive hive, string key, string name)
    {
        if (_keys.TryGetValue(Id(hive, key), out var values)) values.Remove(name);
        Writes.Add($"{key}\\{name} deleted");
    }

    public void DeleteKeyIfEmpty(RegHive hive, string key)
    {
        string id = Id(hive, key);
        bool hasChildren = _keys.Keys.Any(k => k.StartsWith(id + "\\", StringComparison.OrdinalIgnoreCase));
        if (_keys.TryGetValue(id, out var values) && values.Count == 0 && !hasChildren) _keys.Remove(id);
    }

    /// <summary>Seeds a value without recording it as a write.</summary>
    public FakeRegistryStore With(string key, string name, RegData data, RegHive hive = RegHive.CurrentUser)
    {
        var parts = key.Trim('\\').Split('\\');
        for (int i = 1; i <= parts.Length; i++)
        {
            string id = Id(hive, string.Join('\\', parts.Take(i)));
            if (!_keys.ContainsKey(id)) _keys[id] = new(StringComparer.OrdinalIgnoreCase);
        }
        _keys[Id(hive, key)][name] = data;
        return this;
    }

    /// <summary>Every key and value, for whole-state comparisons.</summary>
    public string Dump() => string.Join("\n", _keys.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
        .Select(k => k.Key + " {" + string.Join(", ", k.Value.OrderBy(v => v.Key).Select(v => $"{v.Key}={v.Value}")) + "}"));
}
