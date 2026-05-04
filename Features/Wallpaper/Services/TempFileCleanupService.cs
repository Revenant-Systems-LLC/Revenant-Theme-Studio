using System;
using System.IO;
using System.Threading;

namespace Revenant_Theme_Studio.Features.Wallpaper.Services
{
    public class TempFileCleanupService : IDisposable
    {
        private Timer? _timer;
        private static readonly string[] Prefixes = { "rts_wallpaper_", "rts_upscaled_" };
        private static readonly TimeSpan FileAge = TimeSpan.FromDays(1);

        public void StartDailyCleanup()
        {
            Purge();

            _timer = new Timer(
                _ => Purge(),
                null,
                TimeSpan.FromHours(24),
                TimeSpan.FromHours(24));
        }

        public static int Purge()
        {
            int deleted = 0;
            var tempDir = Path.GetTempPath();
            var cutoff = DateTime.Now - FileAge;

            try
            {
                foreach (var file in Directory.EnumerateFiles(tempDir))
                {
                    var name = Path.GetFileName(file);
                    bool isOurs = false;
                    foreach (var prefix in Prefixes)
                    {
                        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            isOurs = true;
                            break;
                        }
                    }

                    if (!isOurs) continue;

                    try
                    {
                        if (File.GetLastWriteTime(file) < cutoff)
                        {
                            File.Delete(file);
                            deleted++;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return deleted;
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
