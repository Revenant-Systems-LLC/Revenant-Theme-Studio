using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Revenant_Theme_Studio.Services
{
    public record MunCommitRequest(string Target, IReadOnlyList<(int GroupId, byte[] IcoBytes)> Replacements);

    /// <summary>The elevated imageres.dll.mun writer, as the theme engine sees it (faked in tests).</summary>
    public interface IMunWriter
    {
        (bool success, string message) Commit(MunCommitRequest request);
        /// <summary>Puts the preserved Windows original back (undo of every RTS system-icon change).</summary>
        (bool success, string message) Restore(string target);
    }

    public class MunWriterClient : IMunWriter
    {
        private static readonly string HelperPath = Path.Combine(
            AppContext.BaseDirectory, "RTS.MunWriter.exe");

        private static readonly string[] HelperFiles =
        {
            HelperPath,
            Path.Combine(AppContext.BaseDirectory, "RTS.MunWriter.dll"),
            Path.Combine(AppContext.BaseDirectory, "RTS.MunWriter.runtimeconfig.json"),
        };

        [DllImport("sfc.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SfcIsFileProtected(IntPtr hRpcHandle, string FileName);

        public (bool success, string message) Commit(MunCommitRequest request)
        {
            // The helper is framework-dependent: the .exe cannot start without these beside it.
            foreach (var required in HelperFiles)
            {
                if (!File.Exists(required))
                    return (false, $"Helper file missing:\n{required}\n\nRebuild the solution.");
            }

            var validation = ValidateTarget(request.Target);
            if (!validation.success)
                return validation;

            return RunHelper(request, "replace");
        }

        public (bool success, string message) Restore(string target)
        {
            foreach (var required in HelperFiles)
            {
                if (!File.Exists(required))
                    return (false, $"Helper file missing:\n{required}\n\nRebuild the solution.");
            }
            var validation = ValidateTarget(target);
            if (!validation.success)
                return validation;

            var (ok, message) = RunHelper(new MunCommitRequest(target, Array.Empty<(int, byte[])>()), "restore");
            return ok ? (true, "The original Windows icons are back. Restart Explorer or sign out to see them everywhere.") : (false, message);
        }

        private static (bool success, string message) RunHelper(MunCommitRequest request, string mode)
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
                    Mode         = mode,
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
                    return (true, "Changes applied. Programs that are already running keep the old icons " +
                                  "until they restart: restart Explorer or sign out to see them everywhere.");

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
                // 0x800080xx is the .NET host's own error range (hostfxr), raised before any
                // helper code runs. It was previously mislabeled as a Windows Resource
                // Protection error.
                _ when (exitCode & unchecked((int)0xFFFFFF00)) == unchecked((int)0x80008000) =>
                    $"The helper could not start (.NET host error 0x{exitCode:X8}).\n\n" +
                    "Its files may be missing beside the app, or the .NET 8 runtime is not installed.",

                1 => "The helper could not read its job file.",

                5 => $"The helper refused the job: it only edits {targetPath} " +
                    "under Windows\\SystemResources, with icon files from its own job folder.",

                // Staged failures: stage * 100000 + Win32 error (see RTS.MunWriter\Program.cs).
                >= 100000 and < 900000 => StageMessage(exitCode / 100000, exitCode % 100000, targetPath),

                _ => $"Helper exited with code {exitCode}.\n\n" +
                    $"Target: {targetPath}\n\n" +
                    "Try running Revenant Theme Studio as Administrator."
            };

        private static string StageMessage(int stage, int win32, string targetPath)
        {
            string why = win32 == 0 ? "" :
                $"\n\nWindows error {win32}: {new System.ComponentModel.Win32Exception(win32).Message}";
            return stage switch
            {
                1 => "Could not make a private working copy of the icon file. Nothing was changed." + why,
                2 => "Could not write the new icons into the working copy. Nothing was changed." + why,
                3 => "The patched copy failed its check, so it was thrown away. Nothing was changed." + why,
                4 => "Could not get temporary write access to Windows\\SystemResources. Nothing was changed." + why,
                5 => "Could not move the current icon file aside. Nothing was changed." + why,
                6 => "Could not move the new icon file into place, so the original was put back." + why,
                7 => "The new icons are in place, but the file or folder permissions may not be fully " +
                     "restored to the Windows defaults." + why,
                8 => $"CRITICAL: the new file could not be moved in and the original could not be put back. " +
                     $"{targetPath} may be missing; the original is beside it as imageres.dll.mun.rts-original-*. " +
                     "Run 'sfc /scannow' from an admin Command Prompt to restore it." + why,
                9 => "There is nothing to restore: either RTS never changed this file, or Windows has " +
                     "already replaced it with its own." + why,
                _ => $"Helper failed at stage {stage}." + why,
            };
        }
    }
}
