using System.Security.Cryptography;
using System.Text;

namespace NasLicenseManager.Services;

/// <summary>
/// The private signing key, encrypted for the Windows user running the License Manager (DPAPI) in
/// %LOCALAPPDATA%\NasKeyGen — the same file the earlier desktop generator created, so the existing key keeps working.
/// The file is useless if copied elsewhere; back it up from the Signing key page (password-protected PEM).
/// </summary>
public sealed class SigningKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NasPos.KeyGen.v1");

    public SigningKeyStore(string? folder = null)
    {
        Folder = string.IsNullOrWhiteSpace(folder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NasKeyGen")
            : folder;
        Directory.CreateDirectory(Folder);
    }

    public string Folder { get; }
    public string KeyFile => Path.Combine(Folder, "signing-key.bin");
    public bool Exists => File.Exists(KeyFile);

    public ECDsa Create()
    {
        if (Exists) throw new InvalidOperationException("A signing key already exists.");
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Save(key);
        return key;
    }

    public ECDsa Load()
    {
        if (!Exists) throw new InvalidOperationException("No signing key on this PC yet. An administrator must restore the backup on the Signing key page.");
        var pkcs8 = ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            return key;
        }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }
    }

    public static string PublicKeyBase64(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    /// <summary>Password-protected backup (encrypted PKCS#8 PEM) that can be restored on another PC.</summary>
    public string ExportBackupPem(string password)
    {
        using var key = Load();
        return key.ExportEncryptedPkcs8PrivateKeyPem(password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));
    }

    public void ImportBackupPem(string pem, string password, bool overwrite)
    {
        if (Exists && !overwrite) throw new InvalidOperationException("A signing key already exists here.");
        using var key = ECDsa.Create();
        key.ImportFromEncryptedPem(pem, password);
        Save(key);
    }

    private void Save(ECDsa key)
    {
        var pkcs8 = key.ExportPkcs8PrivateKey();
        try { File.WriteAllBytes(KeyFile, ProtectedData.Protect(pkcs8, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }
    }
}
