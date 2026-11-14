using System.Security.Cryptography;
using System.Text;

namespace NasLicenseManager.Services;

/// <summary>
/// The private signing key, encrypted for the Windows user running the License Manager (DPAPI) in
/// %LOCALAPPDATA%\NasKeyGen — the same file the earlier desktop generator created, so the existing key keeps working.
/// The file is useless if copied elsewhere; back it up from the Signing key page (password-protected PEM).
/// Under IIS (app pool identity, no user profile) set Signing:Scope = Machine: the key is then encrypted for this PC,
/// so the folder's permissions are what keep it private — restrict them to the app pool and Administrators.
/// </summary>
public sealed class SigningKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NasPos.KeyGen.v1");
    private readonly DataProtectionScope _scope;

    public SigningKeyStore(string? folder = null, string? scope = null)
    {
        Folder = string.IsNullOrWhiteSpace(folder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NasKeyGen")
            : folder;
        _scope = string.Equals(scope, "Machine", StringComparison.OrdinalIgnoreCase)
            ? DataProtectionScope.LocalMachine
            : DataProtectionScope.CurrentUser;
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
        var pkcs8 = ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), Entropy, _scope);
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
        try { File.WriteAllBytes(KeyFile, ProtectedData.Protect(pkcs8, Entropy, _scope)); }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }
    }
}
