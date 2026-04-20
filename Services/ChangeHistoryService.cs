using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Revenant_Theme_Studio.Models;

namespace Revenant_Theme_Studio.Services
{
    public class ChangeHistoryService
    {
        private readonly string _historyPath;
        private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
        private readonly object _fileLock = new();

        public ChangeHistoryService(ManagedStorageService storage)
        {
            _historyPath = Path.Combine(storage.ManifestsPath, "changes.json");
        }

        public IReadOnlyList<ChangeRecord> GetAll()
        {
            lock (_fileLock)
            {
                if (!File.Exists(_historyPath)) return [];
                var json = File.ReadAllText(_historyPath);
                return JsonSerializer.Deserialize<List<ChangeRecord>>(json) ?? [];
            }
        }

        public ChangeRecord? GetLast() => GetAll().LastOrDefault();

        public void Record(ChangeRecord record)
        {
            lock (_fileLock)
            {
                var all = ReadUnsafe().ToList();
                all.Add(record);
                File.WriteAllText(_historyPath, JsonSerializer.Serialize(all, _jsonOptions));
            }
        }

        public void RemoveLast()
        {
            lock (_fileLock)
            {
                var all = ReadUnsafe().ToList();
                if (all.Count == 0) return;
                all.RemoveAt(all.Count - 1);
                File.WriteAllText(_historyPath, JsonSerializer.Serialize(all, _jsonOptions));
            }
        }

        // Call only while holding _fileLock
        private List<ChangeRecord> ReadUnsafe()
        {
            if (!File.Exists(_historyPath)) return [];
            var json = File.ReadAllText(_historyPath);
            return JsonSerializer.Deserialize<List<ChangeRecord>>(json) ?? [];
        }
    }
}
