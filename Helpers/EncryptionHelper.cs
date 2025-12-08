using System.Security.Cryptography;
using System.Text;
using System.IO;

public static class EncryptionHelper
{
    public static string Encrypt(string plainText, string key)
    {
        if (string.IsNullOrEmpty(key) || Encoding.UTF8.GetByteCount(key) != 32)
            throw new ArgumentException("Invalid encryption key. It must be exactly 32 bytes (256 bits).");

        using (var aes = Aes.Create())
        {
            aes.Key = Encoding.UTF8.GetBytes(key);
            aes.GenerateIV();
            using (var encryptor = aes.CreateEncryptor())
            using (var ms = new MemoryStream())
            {
                ms.Write(aes.IV, 0, aes.IV.Length);
                using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                using (var sw = new StreamWriter(cs))
                {
                    sw.Write(plainText);
                }
                return Convert.ToBase64String(ms.ToArray());
            }
        }
    }

    public static string Decrypt(string cipherText, string key)
    {
        if (string.IsNullOrEmpty(key) || Encoding.UTF8.GetByteCount(key) != 32)
            throw new ArgumentException("Invalid encryption key. It must be exactly 32 bytes (256 bits).");

        var fullCipher = Convert.FromBase64String(cipherText);
        using (var aes = Aes.Create())
        {
            aes.Key = Encoding.UTF8.GetBytes(key);
            var iv = new byte[16];
            Array.Copy(fullCipher, 0, iv, 0, iv.Length);
            aes.IV = iv;
            using (var decryptor = aes.CreateDecryptor())
            using (var ms = new MemoryStream(fullCipher, iv.Length, fullCipher.Length - iv.Length))
            using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
            using (var sr = new StreamReader(cs))
            {
                return sr.ReadToEnd();
            }
        }
    }
}