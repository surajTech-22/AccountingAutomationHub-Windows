using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AccountingAutomationHub.Infrastructure.Persistence;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Protects MFA shared secrets with an RSA private key held in the current Windows user's
/// certificate store. Only ciphertext is stored in SQLite. The public certificate can never
/// decrypt secrets and the private key is never exported by AAH.
/// </summary>
public sealed class NetworkSecretProtector
{
    private readonly ApplicationPreferenceService _settings;

    public NetworkSecretProtector(ApplicationPreferenceService settings) => _settings = settings;

    public string Protect(string clearText)
    {
        var certificate = EnsureCertificate();
        using var rsa = certificate.GetRSAPublicKey() ?? throw new InvalidOperationException("AAH server secret certificate has no RSA public key.");
        var encrypted = rsa.Encrypt(System.Text.Encoding.UTF8.GetBytes(clearText), RSAEncryptionPadding.OaepSHA256);
        return Convert.ToBase64String(encrypted);
    }

    public string Unprotect(string cipherText)
    {
        var certificate = EnsureCertificate();
        using var rsa = certificate.GetRSAPrivateKey() ?? throw new InvalidOperationException("AAH server secret certificate private key is unavailable.");
        var decrypted = rsa.Decrypt(Convert.FromBase64String(cipherText), RSAEncryptionPadding.OaepSHA256);
        return System.Text.Encoding.UTF8.GetString(decrypted);
    }

    public string PublicFingerprint => EnsureCertificate().Thumbprint ?? string.Empty;

    private X509Certificate2 EnsureCertificate()
    {
        var thumbprint = _settings.Get(ApplicationSettingKeys.SecurityServerSecretCertificateThumbprint).Replace(" ", string.Empty);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        if (thumbprint.Length > 0)
        {
            var existing = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false)
                .OfType<X509Certificate2>()
                .FirstOrDefault(x => x.HasPrivateKey && x.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddYears(1));
            if (existing is not null) return existing;
        }

        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=Accounting Automation Hub Server Secret Protection", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DataEncipherment | X509KeyUsageFlags.KeyEncipherment, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var certificate = X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
        store.Add(certificate);
        _settings.Set(ApplicationSettingKeys.SecurityServerSecretCertificateThumbprint, certificate.Thumbprint ?? string.Empty);
        return certificate;
    }
}
