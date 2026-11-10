using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Revenant_Theme_Studio.Features.Themes.Platform;

namespace Revenant_Theme_Studio.Features.Themes.Engine
{
    public enum TxState
    {
        Planned, Snapshotted, Applying, Applied,
        RolledBack, RollbackPartial, Undone, UndonePartial,
    }

    public sealed class SectionResult
    {
        public string Key { get; set; } = "";
        public Decision Decision { get; set; }
        public string? Reason { get; set; }
        public VerifyResult? Status { get; set; }
        public string? Detail { get; set; }
    }

    /// <summary>The permanent record of one theme apply (spec F2 "Record").</summary>
    public sealed class TransactionRecord
    {
        public string TxId { get; set; } = "";
        public string BundleId { get; set; } = "";
        public string BundleName { get; set; } = "";
        public string BundleVersion { get; set; } = "";
        /// <summary>The bundle file that was applied, so undo can re-read section data if needed.</summary>
        public string? BundlePath { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset? FinishedUtc { get; set; }
        public TxState State { get; set; }
        public string? Build { get; set; }
        public List<SectionResult> Sections { get; set; } = new();
        public List<string> Notes { get; set; } = new();
    }

    /// <summary>
    /// Transactions\&lt;txId&gt;\ holds record.json, before.json (state before the apply) and
    /// written.json (state right after). Every write is atomic.
    /// </summary>
    public sealed class TransactionStore
    {
        public string Root { get; }

        public TransactionStore(string root)
        {
            Root = root;
            Directory.CreateDirectory(Root);
        }

        public static TransactionStore Default() => new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Revenant Theme Studio", "Transactions"));

        public static string NewId() => $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

        private string Dir(string txId) => Path.Combine(Root, txId);

        public void Save(TransactionRecord r) => AtomicFile.WriteJson(Path.Combine(Dir(r.TxId), "record.json"), r);
        public void SaveBefore(string txId, List<SectionState> s) => AtomicFile.WriteJson(Path.Combine(Dir(txId), "before.json"), s);
        public void SaveWritten(string txId, List<SectionState> s) => AtomicFile.WriteJson(Path.Combine(Dir(txId), "written.json"), s);

        public List<SectionState>? LoadBefore(string txId) => Load<List<SectionState>>(Path.Combine(Dir(txId), "before.json"));
        public List<SectionState>? LoadWritten(string txId) => Load<List<SectionState>>(Path.Combine(Dir(txId), "written.json"));

        public IReadOnlyList<TransactionRecord> All() =>
            Directory.EnumerateDirectories(Root)
                .Select(d => Load<TransactionRecord>(Path.Combine(d, "record.json")))
                .Where(r => r is not null).Select(r => r!)
                .OrderByDescending(r => r.StartedUtc).ToList();

        /// <summary>The most recent theme still in effect, which "Undo last theme" reverts.</summary>
        public TransactionRecord? LatestApplied() => All().FirstOrDefault(r => r.State == TxState.Applied);

        /// <summary>A transaction a crash or power cut left half done.</summary>
        public TransactionRecord? Interrupted() =>
            All().FirstOrDefault(r => r.State is TxState.Snapshotted or TxState.Applying);

        private static T? Load<T>(string path)
        {
            try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json.Options) : default; }
            catch (JsonException) { return default; }
        }
    }
}
