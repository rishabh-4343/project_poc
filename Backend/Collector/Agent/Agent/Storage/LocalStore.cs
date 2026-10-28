using System.Security.Cryptography;
using System.Text.Json;

namespace Auditor.Agent.Storage;

// AES-GCM sealing with a random 256-bit key kept in <directory>/key.bin, protected by DPAPI (machine scope) so only
// this machine can unwrap it. Sealed layout: version(1) | nonce(12) | tag(16) | ciphertext.
public sealed class LocalCrypto
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] Context = "auditor-agent-v1"u8.ToArray();

    private readonly byte[] _key;

    public LocalCrypto(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "key.bin");

        if (File.Exists(path))
        {
            _key = ProtectedData.Unprotect(File.ReadAllBytes(path), Context, DataProtectionScope.LocalMachine);
            return;
        }

        _key = RandomNumberGenerator.GetBytes(32);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, ProtectedData.Protect(_key, Context, DataProtectionScope.LocalMachine));
        File.Move(temp, path, overwrite: true);
    }

    public byte[] Seal(ReadOnlySpan<byte> plain)
    {
        var output = new byte[1 + NonceSize + TagSize + plain.Length];
        output[0] = Version;
        var nonce = output.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(1 + NonceSize + TagSize), output.AsSpan(1 + NonceSize, TagSize), Context);
        return output;
    }

    // Throws CryptographicException when the data was altered or sealed with another key.
    public byte[] Open(ReadOnlySpan<byte> sealedData)
    {
        if (sealedData.Length < 1 + NonceSize + TagSize || sealedData[0] != Version)
        {
            throw new CryptographicException("Not a sealed agent file.");
        }

        var plain = new byte[sealedData.Length - 1 - NonceSize - TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(sealedData.Slice(1, NonceSize), sealedData[(1 + NonceSize + TagSize)..], sealedData.Slice(1 + NonceSize, TagSize), plain, Context);
        return plain;
    }
}

// Small encrypted JSON files, written atomically (temp file then rename).
public sealed class LocalStore(string directory, LocalCrypto crypto)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public T? Read<T>(string name)
    {
        var path = PathFor(name);
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(crypto.Open(File.ReadAllBytes(path)), Json) : default;
    }

    public void Write<T>(string name, T value)
    {
        Directory.CreateDirectory(directory);
        var path = PathFor(name);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, crypto.Seal(JsonSerializer.SerializeToUtf8Bytes(value, Json)));
        File.Move(temp, path, overwrite: true);
    }

    private string PathFor(string name) => Path.Combine(directory, name + ".enc");
}
