using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Intertube.Stub;

/// <summary>Server-side TLS settings, for implicit TLS or <see cref="Connection.UpgradeToTlsAsync"/>.</summary>
public sealed class TlsOptions
{
    /// <summary>Server certificate, including its private key.</summary>
    public required X509Certificate2 Certificate { get; init; }

    /// <summary>Allowed protocol versions; <see cref="SslProtocols.None"/> lets the OS choose.</summary>
    public SslProtocols EnabledProtocols { get; init; } = SslProtocols.None;

    /// <summary>Ask the client for a certificate.</summary>
    public bool ClientCertificateRequired { get; init; }

    /// <summary>Validates client certificates. Without it, any presented certificate is accepted.</summary>
    public RemoteCertificateValidationCallback? ClientCertificateValidation { get; init; }

    /// <summary>ALPN protocols to offer.</summary>
    public List<SslApplicationProtocol>? ApplicationProtocols { get; init; }

    /// <summary>
    /// A client-side validation callback that trusts exactly <see cref="Certificate"/>, so tests can
    /// talk to a self-signed stub without disabling validation globally.
    /// </summary>
    public RemoteCertificateValidationCallback TrustServerCertificate =>
        (_, certificate, _, _) => certificate is not null &&
            certificate.GetCertHash().AsSpan().SequenceEqual(Certificate.GetCertHash());

    /// <summary>Create TLS options with a fresh self-signed certificate valid for one week.</summary>
    /// <param name="hostName">DNS name for the subject and SAN; 127.0.0.1 and ::1 are always included.</param>
    public static TlsOptions SelfSigned(string hostName = "localhost") =>
        new() { Certificate = CreateSelfSignedCertificate(hostName) };

    /// <summary>Create a self-signed ECDSA P-256 server certificate.</summary>
    public static X509Certificate2 CreateSelfSignedCertificate(string hostName = "localhost")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={hostName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(hostName);
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());

        var now = DateTimeOffset.UtcNow;
        using var ephemeral = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(7));

        // Round-trip through PFX: SChannel on Windows cannot use an ephemeral in-memory key.
        var pfx = ephemeral.Export(X509ContentType.Pfx);
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(pfx, null);
#else
        return new X509Certificate2(pfx);
#endif
    }

    internal SslServerAuthenticationOptions ToServerOptions() => new()
    {
        ServerCertificate = Certificate,
        EnabledSslProtocols = EnabledProtocols,
        ClientCertificateRequired = ClientCertificateRequired,
        RemoteCertificateValidationCallback = ClientCertificateValidation ?? ((_, _, _, _) => true),
        ApplicationProtocols = ApplicationProtocols,
    };

    internal SslClientAuthenticationOptions ToClientOptions(string targetHost) => new()
    {
        TargetHost = targetHost,
        EnabledSslProtocols = EnabledProtocols,
        RemoteCertificateValidationCallback = TrustServerCertificate,
        ApplicationProtocols = ApplicationProtocols,
    };
}
