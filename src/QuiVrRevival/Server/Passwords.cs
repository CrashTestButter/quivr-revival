using System.Security.Cryptography;

namespace QuiVrRevival.Server;

/// <summary>
/// PBKDF2-HMAC-SHA256. Implemented on top of SHA256 because Wine (Proton), where the server
/// runs when the game starts it on Linux, lacks the native PBKDF2 function .NET uses on Windows.
/// </summary>
static class Passwords
{
    const int Iterations = 20_000;
    const int Length = 32;

    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Pbkdf2(System.Text.Encoding.UTF8.GetBytes(password), salt, Iterations);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        var parts = stored?.Split('$');
        if (parts is not ["pbkdf2", var iter, var salt, var hash]) return false;
        byte[] actual = Pbkdf2(System.Text.Encoding.UTF8.GetBytes(password), Convert.FromBase64String(salt), int.Parse(iter));
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(hash));
    }

    // One output block is enough: the derived key is exactly one SHA256 hash long.
    static byte[] Pbkdf2(byte[] password, byte[] salt, int iterations)
    {
        var block = new byte[salt.Length + 4];
        salt.CopyTo(block, 0);
        block[^1] = 1;
        byte[] u = Hmac(password, block);
        byte[] result = (byte[])u.Clone();
        for (int i = 1; i < iterations; i++)
        {
            u = Hmac(password, u);
            for (int j = 0; j < Length; j++) result[j] ^= u[j];
        }
        return result;
    }

    static byte[] Hmac(byte[] key, byte[] message)
    {
        const int blockSize = 64;
        if (key.Length > blockSize) key = SHA256.HashData(key);
        var inner = new byte[blockSize + message.Length];
        var outer = new byte[blockSize + Length];
        for (int i = 0; i < blockSize; i++)
        {
            byte k = i < key.Length ? key[i] : (byte)0;
            inner[i] = (byte)(k ^ 0x36);
            outer[i] = (byte)(k ^ 0x5c);
        }
        message.CopyTo(inner, blockSize);
        SHA256.HashData(inner).CopyTo(outer, blockSize);
        return SHA256.HashData(outer);
    }
}
