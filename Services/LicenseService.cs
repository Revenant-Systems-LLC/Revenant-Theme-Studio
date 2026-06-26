using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Revenant_Theme_Studio.Services
{
    /// <summary>
    /// Server-validated license service. Key authenticity is verified against
    /// https://license.revenantsystems.net/api/validate — no HMAC secret lives
    /// in this binary. A successful activation is cached locally (AES-CBC,
    /// machine-bound) and honoured offline for up to 30 days.
    /// </summary>
    public sealed class LicenseService
    {
        private const string ValidationEndpoint = "https://license.revenantsystems.net/api/validate";
        private const int OfflineGraceDays = 30;

        private static readonly string _keyFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Revenant Theme Studio",
            "license.dat");

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

        public static LicenseService Instance { get; } = new LicenseService();

        public LicenseTier CurrentTier { get; private set; } = LicenseTier.Free;
        public bool IsPro => CurrentTier == LicenseTier.Pro;

        private LicenseService() => TryLoadCachedActivation();

        public async Task<bool> ValidateAndActivateAsync(string rawKey)
        {
            if (string.IsNullOrWhiteSpace(rawKey)) return false;

            var cleaned = rawKey.Trim().ToUpperInvariant();

            try
            {
                var body = JsonSerializer.Serialize(new { key = cleaned });
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync(ValidationEndpoint, content);

                if (!response.IsSuccessStatusCode) return false;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("valid", out var validProp) || !validProp.GetBoolean())
                    return false;

                var tier = root.TryGetProperty("tier", out var tierProp)
                    ? tierProp.GetString() : "pro";

                if (tier != "pro") return false;

                CurrentTier = LicenseTier.Pro;
                SaveActivation(cleaned);
                return true;
            }
            catch { return false; }
        }

        public void Deactivate()
        {
            CurrentTier = LicenseTier.Free;
            try { if (File.Exists(_keyFilePath)) File.Delete(_keyFilePath); } catch { }
        }

        private void TryLoadCachedActivation()
        {
            try
            {
                if (!File.Exists(_keyFilePath)) return;
                var stored  = File.ReadAllBytes(_keyFilePath);
                var iv      = stored[..16];
                var payload = stored[16..];
                using var aes = Aes.Create();
                aes.Key = GetMachineKey();
                var json = Encoding.UTF8.GetString(aes.DecryptCbc(payload, iv));
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("activatedUtc", out var dateProp))
                {
                    var activatedUtc = DateTimeOffset.Parse(dateProp.GetString()!);
                    if (DateTimeOffset.UtcNow - activatedUtc > TimeSpan.FromDays(OfflineGraceDays))
                        return;
                }

                if (root.TryGetProperty("tier", out var tierProp) && tierProp.GetString() == "pro")
                    CurrentTier = LicenseTier.Pro;
            }
            catch { /* corrupted / different machine — stay Free */ }
        }

        private static void SaveActivation(string rawKey)
        {
            try
            {
                var record = JsonSerializer.Serialize(new
                {
                    key          = rawKey,
                    tier         = "pro",
                    activatedUtc = DateTimeOffset.UtcNow.ToString("O")
                });

                Directory.CreateDirectory(Path.GetDirectoryName(_keyFilePath)!);
                using var aes = Aes.Create();
                aes.Key = GetMachineKey();
                aes.GenerateIV();
                var encrypted = aes.EncryptCbc(Encoding.UTF8.GetBytes(record), aes.IV);
                File.WriteAllBytes(_keyFilePath, aes.IV.Concat(encrypted).ToArray());
            }
            catch { /* non-fatal — user re-enters key next launch */ }
        }

        private static byte[] GetMachineKey()
        {
            var entropy = $"RTS-{Environment.MachineName}-v1";
            return SHA256.HashData(Encoding.UTF8.GetBytes(entropy));
        }
    }
}
