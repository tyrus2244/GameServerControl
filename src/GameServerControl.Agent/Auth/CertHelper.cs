using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace GameServerControl.Agent.Auth;

public static class CertHelper
{
    /// <summary>
    /// Loads <paramref name="pfxPath"/> if present, otherwise mints a fresh self-signed
    /// cert valid for 5 years and persists it to disk. Includes SANs for localhost,
    /// the host's Tailscale name, and the live Tailscale IP.
    /// </summary>
    public static X509Certificate2 LoadOrCreate(string pfxPath, string pfxPassword, string[] extraSanHosts, IPAddress[] extraSanIps)
    {
        if (File.Exists(pfxPath))
            return new X509Certificate2(pfxPath, pfxPassword, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=GameServerControl Agent", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true)); // server auth

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        foreach (var h in extraSanHosts) san.AddDnsName(h);
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var ip in extraSanIps) san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());

        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        var bytes = cert.Export(X509ContentType.Pfx, pfxPassword);
        File.WriteAllBytes(pfxPath, bytes);
        return new X509Certificate2(bytes, pfxPassword, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
    }
}
