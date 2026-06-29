using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Revenant_Theme_Studio.Services
{
    /// <summary>
    /// HMAC-SHA256 local license validation.
    /// Key format: RTSP-XXXXXXXX-XXXXXXXX-XXXXXXXX (24 hex chars = 12 bytes)
    ///   Byte[0]      = tier (0x01 = Pro)
    ///   Bytes[1-5]   = random (5 bytes, ensures uniqueness)
    ///   Bytes[6-11]  = first 6 bytes of HMAC-SHA256(bytes[0..5], _secret)
    /// Stored encrypted via AES-CBC with a machine-derived key.
    /// Swap ValidateAndActivate for a server call later — nothing else changes.
    /// </summary>
    public sealed class LicenseService
    {
        private static readonly byte[] _secret =
        {
            0xA3, 0x7F, 0x2C, 0x91, 0xE8, 0x4B, 0xD6, 0x05,
            0x3A, 0xC2, 0x78, 0xBF, 0x19, 0x6E, 0xF4, 0x82,
            0x57, 0x0D, 0xAB, 0x3C, 0x9F, 0xE1, 0x74, 0x28,
            0x6D, 0xB0, 0x45, 0xF3, 0x1A, 0x8C, 0x62, 0xDE
        };

        private static readonly string _keyFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Revenant Theme Studio",
            "license.dat");

        public static LicenseService Instance { get; } = new LicenseService();

        public LicenseTier CurrentTier { get; private set; } = LicenseTier.Free;
        public bool IsPro => CurrentTier == LicenseTier.Pro;

        private LicenseService() => TryLoadSavedKey();
        public bool ValidateAndActivate(string rawKey)
        {
            if (string.IsNullOrWhiteSpace(rawKey)) return false;

            var cleaned = rawKey.Replace("-", "").Replace(" ", "").ToUpperInvariant();
            if (cleaned.StartsWith("RTSP")) cleaned = cleaned[4..];
            if (cleaned.Length != 24) return false;

            try
            {
                var bytes = Convert.FromHexString(cleaned);
                var payload  = bytes[..6];
                var checksum = bytes[6..];

                using var hmac = new HMACSHA256(_secret);
                var computed = hmac.ComputeHash(payload);
                if (!computed.Take(6).SequenceEqual(checksum)) return false;
                if (bytes[0] != 0x01) return false; // 0x01 = Pro tier

                CurrentTier = LicenseTier.Pro;
                SaveKey(rawKey);
                return true;
            }
            catch { return false; }
        }

        public void Deactivate()
        {
            CurrentTier = LicenseTier.Free;
            try { if (File.Exists(_keyFilePath)) File.Delete(_keyFilePath); } catch { }
        }

        /// <summary>
        /// Dev tool — generate a valid Pro key. Call from a scratch console or unit test.
        /// Remove or guard before public release if desired.
        /// </summary>
        internal static string GenerateProKey()
        {
            var random = new byte[5];
            RandomNumberGenerator.Fill(random);
            var payload = new byte[] { 0x01 }.Concat(random).ToArray();
            using var hmac = new HMACSHA256(_secret);
            var sig = hmac.ComputeHash(payload).Take(6).ToArray();
            var hex = Convert.ToHexString(payload.Concat(sig).ToArray());
            return $"RTSP-{hex[..8]}-{hex[8..16]}-{hex[16..24]}";
        }
        private void TryLoadSavedKey()
        {
            try
            {
                if (!File.Exists(_keyFilePath)) return;
                var stored  = File.ReadAllBytes(_keyFilePath);
                var iv      = stored[..16];
                var payload = stored[16..];
                using var aes = Aes.Create();
                aes.Key = GetMachineKey();
                var decrypted = aes.DecryptCbc(payload, iv);
                ValidateAndActivate(Encoding.UTF8.GetString(decrypted));
            }
            catch { /* corrupted / different machine — stay Free */ }
        }

        private static void SaveKey(string rawKey)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_keyFilePath)!);
                using var aes = Aes.Create();
                aes.Key = GetMachineKey();
                aes.GenerateIV();
                var encrypted = aes.EncryptCbc(Encoding.UTF8.GetBytes(rawKey), aes.IV);
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
