using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace Revenant_Theme_Studio.Features.Themes.Platform
{
    public enum RegHive { CurrentUser, LocalMachine }

    /// <summary>
    /// A registry value exactly as stored: its kind plus raw data. Strings are kept unexpanded,
    /// DWORDs and QWORDs unsigned. JSON-friendly, so snapshots can be saved as-is.
    /// </summary>
    public sealed record RegData
    {
        public RegistryValueKind Kind { get; init; }
        public string? Text { get; init; }
        public string[]? Lines { get; init; }
        public ulong? Number { get; init; }
        public byte[]? Bytes { get; init; }

        /// <summary>REG_NONE, REG_LINK and other kinds .NET cannot round-trip. Never overwritten.</summary>
        public bool Unreadable => Kind is RegistryValueKind.None or RegistryValueKind.Unknown;

        public static RegData Dword(uint v) => new() { Kind = RegistryValueKind.DWord, Number = v };
        public static RegData Qword(ulong v) => new() { Kind = RegistryValueKind.QWord, Number = v };
        public static RegData Str(string v) => new() { Kind = RegistryValueKind.String, Text = v };
        public static RegData Expand(string v) => new() { Kind = RegistryValueKind.ExpandString, Text = v };
        public static RegData Binary(byte[] v) => new() { Kind = RegistryValueKind.Binary, Bytes = v };
        public static RegData Multi(string[] v) => new() { Kind = RegistryValueKind.MultiString, Lines = v };

        public bool SameAs(RegData? other) =>
            other is not null && Kind == other.Kind && Text == other.Text && Number == other.Number &&
            (Lines ?? Array.Empty<string>()).SequenceEqual(other.Lines ?? Array.Empty<string>()) &&
            (Bytes ?? Array.Empty<byte>()).SequenceEqual(other.Bytes ?? Array.Empty<byte>());

        public override string ToString() => Kind switch
        {
            RegistryValueKind.DWord or RegistryValueKind.QWord => $"{Kind} 0x{Number:X}",
            RegistryValueKind.Binary => $"Binary[{Bytes?.Length ?? 0}]",
            RegistryValueKind.MultiString => $"MultiString[{Lines?.Length ?? 0}]",
            _ => $"{Kind} \"{Text}\"",
        };
    }

    /// <summary>Registry access the theme engine can fake in tests.</summary>
    public interface IRegistryStore
    {
        bool KeyExists(RegHive hive, string key);
        /// <summary>The value, or null if it does not exist. The unnamed default value is "".</summary>
        RegData? Get(RegHive hive, string key, string name);
        IReadOnlyList<string> ValueNames(RegHive hive, string key);
        /// <summary>Writes the value, creating the key and any missing parents.</summary>
        void Set(RegHive hive, string key, string name, RegData data);
        void DeleteValue(RegHive hive, string key, string name);
        /// <summary>Deletes the key only if it has no values and no subkeys.</summary>
        void DeleteKeyIfEmpty(RegHive hive, string key);
    }

    /// <summary>The real registry. The engine writes HKCU only; HKLM is read-only here.</summary>
    public sealed class RegistryStore : IRegistryStore
    {
        private static RegistryKey Root(RegHive hive) =>
            hive == RegHive.CurrentUser ? Registry.CurrentUser : Registry.LocalMachine;

        public bool KeyExists(RegHive hive, string key)
        {
            using var k = Root(hive).OpenSubKey(key);
            return k is not null;
        }

        public RegData? Get(RegHive hive, string key, string name)
        {
            using var k = Root(hive).OpenSubKey(key);
            if (k is null || !k.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return null;

            RegistryValueKind kind;
            try { kind = k.GetValueKind(name); }
            catch (Exception) { return new RegData { Kind = RegistryValueKind.Unknown }; }

            object? raw = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return kind switch
            {
                RegistryValueKind.DWord => RegData.Dword(unchecked((uint)(int)raw!)),
                RegistryValueKind.QWord => RegData.Qword(unchecked((ulong)(long)raw!)),
                RegistryValueKind.String => RegData.Str((string)raw!),
                RegistryValueKind.ExpandString => RegData.Expand((string)raw!),
                RegistryValueKind.MultiString => RegData.Multi((string[])raw!),
                RegistryValueKind.Binary => RegData.Binary((byte[])raw!),
                _ => new RegData { Kind = kind },
            };
        }

        public IReadOnlyList<string> ValueNames(RegHive hive, string key)
        {
            using var k = Root(hive).OpenSubKey(key);
            return k?.GetValueNames() ?? Array.Empty<string>();
        }

        public void Set(RegHive hive, string key, string name, RegData data)
        {
            if (hive != RegHive.CurrentUser) throw new InvalidOperationException("The theme engine writes HKCU only.");
            if (data.Unreadable) throw new InvalidOperationException($"Refusing to write a {data.Kind} value.");
            using var k = Root(hive).CreateSubKey(key, writable: true)
                ?? throw new InvalidOperationException($"Could not open HKCU\\{key}");
            object value = data.Kind switch
            {
                RegistryValueKind.DWord => unchecked((int)(uint)data.Number!.Value),
                RegistryValueKind.QWord => unchecked((long)data.Number!.Value),
                RegistryValueKind.MultiString => data.Lines ?? Array.Empty<string>(),
                RegistryValueKind.Binary => data.Bytes ?? Array.Empty<byte>(),
                _ => data.Text ?? "",
            };
            k.SetValue(name, value, data.Kind);
        }

        public void DeleteValue(RegHive hive, string key, string name)
        {
            if (hive != RegHive.CurrentUser) throw new InvalidOperationException("The theme engine writes HKCU only.");
            using var k = Root(hive).OpenSubKey(key, writable: true);
            k?.DeleteValue(name, throwOnMissingValue: false);
        }

        public void DeleteKeyIfEmpty(RegHive hive, string key)
        {
            if (hive != RegHive.CurrentUser) throw new InvalidOperationException("The theme engine writes HKCU only.");
            bool empty;
            using (var k = Root(hive).OpenSubKey(key))
                empty = k is not null && k.SubKeyCount == 0 && k.ValueCount == 0;
            if (empty) Root(hive).DeleteSubKey(key, throwOnMissingSubKey: false);
        }
    }

    /// <summary>
    /// One registry value's state before a change: its data (null = did not exist) and which keys
    /// on its path did not exist, so undo can remove exactly what was created and nothing else.
    /// </summary>
    public sealed class RegSnapshotItem
    {
        public RegHive Hive { get; init; }
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public RegData? Data { get; init; }
        /// <summary>Keys on the path that did not exist, deepest first.</summary>
        public List<string> CreatedKeys { get; init; } = new();

        public bool Unreadable => Data?.Unreadable == true;
    }

    public static class RegSnapshot
    {
        public static RegSnapshotItem Capture(IRegistryStore store, RegHive hive, string key, string name)
        {
            var missing = new List<string>();
            for (string? k = key; !string.IsNullOrEmpty(k) && !store.KeyExists(hive, k); k = Parent(k))
                missing.Add(k);
            return new RegSnapshotItem
            {
                Hive = hive, Key = key, Name = name,
                Data = missing.Count == 0 ? store.Get(hive, key, name) : null,
                CreatedKeys = missing,
            };
        }

        /// <summary>Puts the value back exactly. Unreadable values were never written, so are left alone.</summary>
        public static void Restore(IRegistryStore store, RegSnapshotItem item)
        {
            if (item.Unreadable) return;
            if (item.Data is null) store.DeleteValue(item.Hive, item.Key, item.Name);
            else store.Set(item.Hive, item.Key, item.Name, item.Data);
            foreach (var k in item.CreatedKeys) store.DeleteKeyIfEmpty(item.Hive, k);
        }

        /// <summary>True if the live value still equals what was written (used by the undo conflict check).</summary>
        public static bool StillEquals(IRegistryStore store, RegSnapshotItem written) =>
            written.Data is null ? store.Get(written.Hive, written.Key, written.Name) is null
                                 : written.Data.SameAs(store.Get(written.Hive, written.Key, written.Name));

        private static string? Parent(string key)
        {
            int i = key.LastIndexOf('\\');
            return i > 0 ? key[..i] : null;
        }
    }
}
