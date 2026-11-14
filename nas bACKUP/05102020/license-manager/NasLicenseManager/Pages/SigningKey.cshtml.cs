using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NasLicenseManager.Services;

namespace NasLicenseManager.Pages;

/// <summary>Administrators: see the public key, back up the private key, or restore it on a new PC.</summary>
public class SigningKeyModel(SigningKeyStore keys) : PageModel
{
    /// <summary>The public key built into NAS POS Pro (LicenseTrust.ProductionPublicKey).</summary>
    public const string AppPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEFuAy6qCu9pm48I+Cahmcz7LI/U8sWeJM3gzIu6g1LKyH39quyUNvqObLkfDNy4sUoR4GnX2PsGTfYoBxRdMRgg==";

    public bool Exists => keys.Exists;
    public string Folder => keys.Folder;
    public string? PublicKey { get; private set; }
    public bool MatchesApp => PublicKey == AppPublicKey;
    public string? Error { get; set; }

    public void OnGet() => LoadPublicKey();

    private void LoadPublicKey()
    {
        if (!keys.Exists) return;
        try { using var k = keys.Load(); PublicKey = SigningKeyStore.PublicKeyBase64(k); }
        catch (Exception ex) { Error = "The signing key file can't be opened by this Windows user: " + ex.Message; }
    }

    public IActionResult OnPostBackup(string password, string confirm)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 10) { TempData["error"] = "Use a backup password of at least 10 characters."; return RedirectToPage(); }
        if (password != confirm) { TempData["error"] = "The passwords do not match."; return RedirectToPage(); }
        var pem = keys.ExportBackupPem(password);
        return File(Encoding.ASCII.GetBytes(pem), "application/x-pem-file", $"nas-license-signing-key-{DateTime.Today:yyyyMMdd}.pem");
    }

    public async Task<IActionResult> OnPostRestoreAsync(IFormFile? file, string password, bool overwrite)
    {
        if (file is null || file.Length is 0 or > 20_000) { TempData["error"] = "Choose the .pem backup file."; return RedirectToPage(); }
        using var reader = new StreamReader(file.OpenReadStream());
        try
        {
            keys.ImportBackupPem(await reader.ReadToEndAsync(), password ?? "", overwrite);
            TempData["ok"] = "Signing key restored.";
        }
        catch (Exception ex) { TempData["error"] = "Could not restore: " + ex.Message; }
        return RedirectToPage();
    }
}
