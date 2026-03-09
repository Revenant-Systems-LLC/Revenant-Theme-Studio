using System;

namespace Revenant_Theme_Studio.Models
{
    public enum IconTargetType
    {
        Folder,
        Drive,
        Shell
    }

    public class ChangeRecord
    {
        public required string BackupId { get; init; }
        public required IconTargetType TargetType { get; init; }
        public required string TargetPath { get; init; }
        public string? PreviousValue { get; init; }
        public string? NewValue { get; init; }
        public DateTimeOffset Timestamp { get; init; }
    }
}
