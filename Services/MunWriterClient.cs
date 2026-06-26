using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Revenant_Theme_Studio.Services
{
    public record MunCommitRequest(string Target, IReadOnlyList<(int GroupId, byte[] IcoBytes)> Replacements);

    public class MunWriterClient
    {
        private static readonly string HelperPath = Path.Combine(
            AppContext.BaseDirectory, "RTS.MunWriter.exe");

        [DllImport("sfc.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SfcIsFileProtected(IntPtr hRpcHandle, string FileName);

        public (bool success, string message) Commit(MunCommitRequest request)
        {
            if (!File.Exists(HelperPath))
                return (false, $"Helper not found at:\n{HelperPath}\n\nRebuild the solution.");

            var validation = ValidateTarget(request.Target);
            if (!validation.success)
                return validation;

            return RunHelper(request);
        }

        private static (bool success, string message) RunHelper(MunCommitRequest request)
        {
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
                    Verb            = "runas",
                    // rws-suppress: RWS-EXEC-002 UseShellExecute=true required for Verb="runas" UAC elevation
                    UseShellExecute = true,
                    CreateNoWindow  = false,
                    WindowStyle     = ProcessWindowStyle.Hidden
                };

                Process? proc;
                try
                {
                    // rws-suppress: RWS-EXEC-001 psi uses a fixed AppContext-relative helper + GUID temp job file, no user-controlled path
                    proc = Process.Start(psi);
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    return (false, "UAC prompt was cancelled.");
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    return (false, $"Failed to launch elevated helper: {ex.Message}\n\n" +
                        "Try running Revenant Theme Studio as Administrator.");
                }

                if (proc is null)
                    return (false, "Helper process failed to start.\n\nTry running Revenant Theme Studio as Administrator.");

                proc.WaitForExit();

                if (proc.ExitCode == 0)
                    return (true, "Changes applied successfully.");

                return (false, GetErrorMessage(proc.ExitCode, request.Target));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        private static (bool success, string message) ValidateTarget(string targetPath)
        {
            if (!Path.IsPathFullyQualified(targetPath))
                return (false, $"Target path must be absolute:\n{targetPath}");

            if (!File.Exists(targetPath))
                return (false, $"Target file not found:\n{targetPath}");

            try
            {
                using var fs = File.OpenRead(targetPath);
            }
            catch (UnauthorizedAccessException)
            {
                return (false, $"Access denied to:\n{targetPath}\n\n" +
                    "The file may be locked. Close Explorer and try again.");
            }
            catch (IOException ex)
            {
                return (false, $"Cannot access file:\n{targetPath}\n\n{ex.Message}\n\n" +
                    "The file is in use. Close Explorer and try again.");
            }

            return (true, string.Empty);
        }

        private static bool IsWrpProtected(string filePath)
        {
            try
            {
                return SfcIsFileProtected(IntPtr.Zero, filePath);
            }
            catch
            {
                // If the API call fails, let the helper attempt the operation
                // and surface a real error rather than blocking optimistically.
                return false;
            }
        }

        private static string GetErrorMessage(int exitCode, string targetPath) =>
            exitCode switch
            {
                -2147450726 => "Windows Resource Protection blocked the update.\n\n" +
                    "Run 'sfc /scannow' from an admin Command Prompt, then try again.",

                2 => "Permission modification failed.\n\n" +
                    "Try running Revenant Theme Studio as Administrator.",

                3 => $"Resource update failed for:\n{targetPath}\n\n" +
                    "The file may be in use or corrupted. Close Explorer and try again.",

                4 => $"Resource commit failed for:\n{targetPath}\n\n" +
                    "Check disk space and file permissions, then try again.",

                _ => $"Helper exited with code {exitCode}.\n\n" +
                    $"Target: {targetPath}\n\n" +
                    "Try running Revenant Theme Studio as Administrator."
            };
    }
}
