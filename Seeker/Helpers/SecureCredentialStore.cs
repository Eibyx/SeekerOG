using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Seeker.Helpers;
using System;
using System.Text;

namespace Seeker
{
    /// <summary>
    /// Encrypts the stored Soulseek password with an AES-256/GCM key that lives in the Android Keystore
    /// (hardware backed where available, never exported). Only the ciphertext is written to
    /// SharedPreferences. Values written by older versions (plaintext) are read once and re-saved
    /// encrypted. If the key is unavailable (e.g. preferences restored from a backup on another
    /// device) decryption fails safely and the user simply logs in again.
    /// The password is only ever sent to the official Soulseek server by Soulseek.NET.
    /// </summary>
    public static class SecureCredentialStore
    {
        private const string KeyAlias = "seekerog_credentials_v1";
        private const string Prefix = "og1:";
        private const string Transformation = "AES/GCM/NoPadding";

        public static bool IsEncrypted(string stored) => stored != null && stored.StartsWith(Prefix, StringComparison.Ordinal);

        private static IKey GetOrCreateKey()
        {
            var ks = KeyStore.GetInstance("AndroidKeyStore")!;
            ks.Load(null);
            if (ks.ContainsAlias(KeyAlias))
            {
                var entry = (KeyStore.SecretKeyEntry)ks.GetEntry(KeyAlias, null)!;
                return entry.SecretKey!;
            }
            var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
            var spec = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeGcm)!
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
                .SetKeySize(256)!
                .Build();
            generator.Init(spec);
            return generator.GenerateKey()!;
        }

        private static string? lastPlain;
        private static string? lastProtected;

        /// <summary>Same as Protect but avoids a keystore round trip when the password did not change.</summary>
        public static string ProtectCached(string plain)
        {
            if (plain == lastPlain && lastProtected != null)
            {
                return lastProtected;
            }
            string result = Protect(plain);
            lastPlain = plain;
            lastProtected = result;
            return result;
        }

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain))
            {
                return string.Empty;
            }
            try
            {
                var cipher = Cipher.GetInstance(Transformation)!;
                cipher.Init(CipherMode.EncryptMode, GetOrCreateKey());
                byte[] iv = cipher.GetIV()!;
                byte[] ct = cipher.DoFinal(Encoding.UTF8.GetBytes(plain))!;
                var blob = new byte[1 + iv.Length + ct.Length];
                blob[0] = (byte)iv.Length;
                Buffer.BlockCopy(iv, 0, blob, 1, iv.Length);
                Buffer.BlockCopy(ct, 0, blob, 1 + iv.Length, ct.Length);
                return Prefix + Convert.ToBase64String(blob);
            }
            catch (Exception e)
            {
                // Extremely rare (broken keystore implementations). Never lose the user's login:
                // fall back to the legacy behaviour of the original app.
                Logger.Debug("credential encryption unavailable: " + e.GetType().Name);
                return plain;
            }
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored))
            {
                return string.Empty;
            }
            if (!IsEncrypted(stored))
            {
                return stored; // legacy plaintext
            }
            try
            {
                byte[] blob = Convert.FromBase64String(stored.Substring(Prefix.Length));
                int ivLen = blob[0];
                var iv = new byte[ivLen];
                Buffer.BlockCopy(blob, 1, iv, 0, ivLen);
                var cipher = Cipher.GetInstance(Transformation)!;
                cipher.Init(CipherMode.DecryptMode, GetOrCreateKey(), new GCMParameterSpec(128, iv));
                byte[] plain = cipher.DoFinal(blob, 1 + ivLen, blob.Length - 1 - ivLen)!;
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception e)
            {
                Logger.Debug("credential decryption failed: " + e.GetType().Name);
                return string.Empty;
            }
        }
    }
}
