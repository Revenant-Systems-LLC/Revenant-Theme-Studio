using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Revenant_Theme_Studio.Services
{
    public record MunCommitRequest(string Target, IReadOnlyList<(int GroupId, byte[] IcoBytes)> Replacements);

    public class MunWriterClient
    {
        private static readonly string HelperPath = Path.Combine(
            AppContext.BaseDirectory, "RTS.MunWriter.exe");

        public (bool success, string message) Commit(MunCommitRequest request)
        {
            if (!File.Exists(HelperPath))
                return (false, $"Helper not found at:\n{HelperPath}\n\nRebuild the solution.");

            // Validate target is an absolute path to an existing file before handing it to the helper.
            if (!Path.IsPathFullyQualified(request.Target) || !File.Exists(request.Target))
                return (false, $"Target file not found or path is not absolute:\n{request.Target}");

            string tempDir = Path.Combine(Path.GetTempPath(), $"rts_mun_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // Write staged ICO files to temp dir
                var replacements = new List<object>();
                foreach (var (groupId, icoBytes) in request.Replacements)
                {
                    string icoPath = Path.Combine(tempDir, $"group{groupId}.ico");
                    File.WriteAllBytes(icoPath, icoBytes);
                    replacements.Add(new { GroupId = groupId, IcoPath = icoPath });
                }

                // Write job JSON
                string jobPath = Path.Combine(tempDir, "job.json");
                File.WriteAllText(jobPath, JsonSerializer.Serialize(new
                {
                    Target       = request.Target,
                    Replacements = replacements
                }, new JsonSerializerOptions { WriteIndented = true }));

                // HelperPath: fixed AppContext-relative binary, no user input.
                // jobPath: GUID-scoped temp file written above, no user input in the path.
                var psi = new ProcessStartInfo(HelperPath, $"\"{jobPath}\"")
                {
                    Verb                   = "runas",
                    // RWS-suppress: RWS-EXEC-002 UseShellExecute=true required for Verb="runas" UAC elevation
                    UseShellExecute        = true,
                    CreateNoWindow         = false,
                    WindowStyle            = ProcessWindowStyle.Hidden
                };

                Process proc;
                // RWS-suppress: RWS-EXEC-001 psi uses a fixed AppContext-relative helper + GUID temp job file, no user-controlled path
                try   { proc = Process.Start(psi)!; }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                    { return (false, "UAC prompt was cancelled."); }

                proc.WaitForExit();

                if (proc.ExitCode == 0)
                    return (true, "Changes applied successfully.");

                return (false, $"Helper exited with code {proc.ExitCode}.\n" +
                    "Check that imageres.dll.mun exists and Windows is not blocking access.");
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
