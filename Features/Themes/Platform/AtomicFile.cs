using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Revenant_Theme_Studio.Features.Themes.Platform
{
    /// <summary>
    /// Writes that never leave a half-written file: write a temp file beside the target, then
    /// replace the target in one rename. A crash leaves either the old file or the new one.
    /// </summary>
    public static class AtomicFile
    {
        public static void WriteAllBytes(string path, byte[] data)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(dir);
            string tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(data);
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        public static void WriteAllText(string path, string text) =>
            WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));

        public static void WriteJson<T>(string path, T value) =>
            WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, Json.Options));
    }

    /// <summary>One JSON configuration for everything the theme engine stores.</summary>
    public static class Json
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
    }
}
