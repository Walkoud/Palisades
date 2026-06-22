using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Palisades.Services
{
    public static class EncryptionService
    {
        private const int SALT_SIZE = 16;
        private const int ITERATIONS = 100000;
        private const int KEY_SIZE = 32;
        private const int IV_SIZE = 16;

        public static string HashPassword(string password)
        {
            byte[] salt = new byte[SALT_SIZE];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(salt);

            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt, ITERATIONS, HashAlgorithmName.SHA256, KEY_SIZE);

            byte[] result = new byte[SALT_SIZE + KEY_SIZE];
            Buffer.BlockCopy(salt, 0, result, 0, SALT_SIZE);
            Buffer.BlockCopy(hash, 0, result, SALT_SIZE, KEY_SIZE);

            return Convert.ToBase64String(result);
        }

        public static bool VerifyPassword(string password, string passwordHash)
        {
            try
            {
                byte[] stored = Convert.FromBase64String(passwordHash);
                if (stored.Length != SALT_SIZE + KEY_SIZE) return false;

                byte[] salt = new byte[SALT_SIZE];
                Buffer.BlockCopy(stored, 0, salt, 0, SALT_SIZE);

                byte[] expected = new byte[KEY_SIZE];
                Buffer.BlockCopy(stored, SALT_SIZE, expected, 0, KEY_SIZE);

                byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(password), salt,
                    ITERATIONS, HashAlgorithmName.SHA256, KEY_SIZE);

                return CryptographicOperations.FixedTimeEquals(expected, actual);
            }
            catch { return false; }
        }

        public static string Encrypt(string plaintext, string password)
        {
            byte[] iv = new byte[IV_SIZE];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(iv);

            byte[] key = DeriveKey(password, iv);

            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();
            byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
            byte[] cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

            byte[] result = new byte[IV_SIZE + cipherBytes.Length];
            Buffer.BlockCopy(iv, 0, result, 0, IV_SIZE);
            Buffer.BlockCopy(cipherBytes, 0, result, IV_SIZE, cipherBytes.Length);

            return Convert.ToBase64String(result);
        }

        public static string? Decrypt(string ciphertext, string password)
        {
            try
            {
                byte[] data = Convert.FromBase64String(ciphertext);
                if (data.Length < IV_SIZE) return null;

                byte[] iv = new byte[IV_SIZE];
                Buffer.BlockCopy(data, 0, iv, 0, IV_SIZE);

                byte[] key = DeriveKey(password, iv);

                using var aes = Aes.Create();
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using var decryptor = aes.CreateDecryptor();
                byte[] cipherBytes = new byte[data.Length - IV_SIZE];
                Buffer.BlockCopy(data, IV_SIZE, cipherBytes, 0, cipherBytes.Length);

                byte[] plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch { return null; }
        }

        private static byte[] DeriveKey(string password, byte[] salt)
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), salt,
                ITERATIONS, HashAlgorithmName.SHA256, KEY_SIZE);
        }
    }
}
