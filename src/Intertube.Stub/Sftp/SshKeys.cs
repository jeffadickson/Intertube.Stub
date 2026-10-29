using System.Security.Cryptography;
using Intertube.Stub.Sftp.Ssh;

namespace Intertube.Stub.Sftp;

/// <summary>
/// An SSH public key in OpenSSH's one-line form (<c>ssh-ed25519 AAAA... comment</c>), as found in
/// <c>authorized_keys</c>, <c>known_hosts</c> and <c>*.pub</c> files. ssh-ed25519, ecdsa-sha2-nistp256/384/521
/// and ssh-rsa keys can be verified.
/// </summary>
public sealed class SshPublicKey : IEquatable<SshPublicKey>
{
    private readonly byte[] _blob;

    private SshPublicKey(string algorithm, byte[] blob, string? comment)
    {
        Algorithm = algorithm;
        _blob = blob;
        Comment = comment;
    }

    /// <summary>The key type, such as <c>ssh-ed25519</c>.</summary>
    public string Algorithm { get; }

    /// <summary>The trailing comment of the line it was parsed from, if any.</summary>
    public string? Comment { get; }

    /// <summary>The key in SSH wire format.</summary>
    public ReadOnlyMemory<byte> Blob => _blob;

    /// <summary>The OpenSSH SHA-256 fingerprint, as <c>ssh-keygen -l</c> prints it: <c>SHA256:...</c>.</summary>
    public string Fingerprint => "SHA256:" + Convert.ToBase64String(SHA256.HashData(_blob)).TrimEnd('=');

    /// <summary>Parse one <c>type base64 [comment]</c> line, such as the contents of an <c>id_ed25519.pub</c> file.</summary>
    public static SshPublicKey Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var parts = line.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new FormatException("Expected an OpenSSH public key line: \"type base64 [comment]\".");
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException ex)
        {
            throw new FormatException("The public key is not valid base64.", ex);
        }
        var key = FromBlob(blob, parts.Length > 2 ? parts[2] : null);
        if (key.Algorithm != parts[0]) throw new FormatException($"The line says {parts[0]} but the key is {key.Algorithm}.");
        return key;
    }

    /// <summary>Read the first key from a <c>*.pub</c> or <c>authorized_keys</c> file.</summary>
    public static SshPublicKey Load(string path) =>
        Parse(File.ReadLines(path).First(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')));

    internal static SshPublicKey FromBlob(ReadOnlySpan<byte> blob, string? comment = null)
    {
        string algorithm;
        try
        {
            var reader = new SshReader(blob);
            algorithm = reader.Utf8();
        }
        catch (SshProtocolException ex)
        {
            throw new FormatException("The public key blob is malformed.", ex);
        }
        return new SshPublicKey(algorithm, blob.ToArray(), comment);
    }

    /// <summary>
    /// Whether this key can check a signature of type <paramref name="algorithm"/>: the key's own type,
    /// or for RSA keys rsa-sha2-256, rsa-sha2-512 or (SHA-1) ssh-rsa.
    /// </summary>
    internal bool Supports(string? algorithm) => Algorithm switch
    {
        "ssh-rsa" => algorithm is "rsa-sha2-256" or "rsa-sha2-512" or "ssh-rsa",
        "ssh-ed25519" or "ecdsa-sha2-nistp256" or "ecdsa-sha2-nistp384" or "ecdsa-sha2-nistp521" => algorithm == Algorithm,
        _ => false,
    };

    /// <summary>
    /// Check an SSH signature blob (<c>string algorithm, string signature</c>) over <paramref name="data"/>.
    /// The blob's algorithm must be <paramref name="algorithm"/>, the one the request declared (RFC 8332 section 3.2).
    /// </summary>
    internal bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signatureBlob, string algorithm)
    {
        if (!Supports(algorithm)) return false;
        try
        {
            var signature = new SshReader(signatureBlob);
            var signatureAlgorithm = signature.Utf8();
            var signatureBytes = signature.String();
            if (signatureAlgorithm != algorithm || !signature.AtEnd) return false;
            var key = new SshReader(_blob);
            key.Utf8();
            switch (Algorithm)
            {
                case "ssh-ed25519":
                    return Ed25519.Verify(key.String(), data, signatureBytes);

                case "ecdsa-sha2-nistp256" or "ecdsa-sha2-nistp384" or "ecdsa-sha2-nistp521":
                {
                    var curveName = key.Utf8();
                    if ("ecdsa-sha2-" + curveName != Algorithm) return false;
                    var (curve, size, hash) = EcdsaCurve(curveName);
                    using var ecdsa = ECDsa.Create(new ECParameters { Curve = curve, Q = DecodePoint(key.String(), size) });
                    var rs = new SshReader(signatureBytes);
                    var p1363 = new byte[size * 2];
                    rs.UnsignedFixed(size).CopyTo(p1363, 0);
                    rs.UnsignedFixed(size).CopyTo(p1363, size);
                    return rs.AtEnd && ecdsa.VerifyData(data, p1363, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                }

                case "ssh-rsa":
                {
                    var hash = algorithm switch
                    {
                        "rsa-sha2-256" => HashAlgorithmName.SHA256,
                        "rsa-sha2-512" => HashAlgorithmName.SHA512,
                        _ => HashAlgorithmName.SHA1,
                    };
                    var exponent = key.String().ToArray();
                    var modulus = key.String().TrimStart((byte)0).ToArray();
                    using var rsa = RSA.Create(new RSAParameters { Exponent = exponent, Modulus = modulus });
                    return rsa.VerifyData(data, signatureBytes, hash, RSASignaturePadding.Pkcs1);
                }

                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is SshProtocolException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    internal static (ECCurve Curve, int Size, HashAlgorithmName Hash) EcdsaCurve(string name) => name switch
    {
        "nistp256" => (ECCurve.NamedCurves.nistP256, 32, HashAlgorithmName.SHA256),
        "nistp384" => (ECCurve.NamedCurves.nistP384, 48, HashAlgorithmName.SHA384),
        "nistp521" => (ECCurve.NamedCurves.nistP521, 66, HashAlgorithmName.SHA512),
        _ => throw new SshProtocolException($"Unsupported curve {name}."),
    };

    internal static ECPoint DecodePoint(ReadOnlySpan<byte> encoded, int size)
    {
        if (encoded.Length != 1 + 2 * size || encoded[0] != 4) throw new SshProtocolException("Expected an uncompressed elliptic-curve point.");
        return new ECPoint { X = encoded.Slice(1, size).ToArray(), Y = encoded.Slice(1 + size, size).ToArray() };
    }

    /// <summary>The OpenSSH one-line form, <c>type base64</c> plus the comment when there is one.</summary>
    public override string ToString() =>
        $"{Algorithm} {Convert.ToBase64String(_blob)}" + (string.IsNullOrEmpty(Comment) ? "" : " " + Comment);

    public bool Equals(SshPublicKey? other) => other is not null && _blob.AsSpan().SequenceEqual(other._blob);

    public override bool Equals(object? obj) => Equals(obj as SshPublicKey);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_blob);
        return hash.ToHashCode();
    }
}

/// <summary>
/// An ECDSA or RSA key pair, made on the fly. Use it as a server's host key, or as a client identity
/// so a test does not need <c>ssh-keygen</c>: give <see cref="PublicKey"/> to the server and
/// <see cref="PrivateKeyPem"/> (which OpenSSH, SSH.NET and most other clients read) to the client.
/// </summary>
public sealed class SshKeyPair : IDisposable
{
    private readonly AsymmetricAlgorithm _key;

    private SshKeyPair(AsymmetricAlgorithm key, string? comment)
    {
        _key = key;
        var blob = new SshWriter();
        switch (key)
        {
            case ECDsa ecdsa:
            {
                var parameters = ecdsa.ExportParameters(false);
                var curve = parameters.Q.X!.Length switch { 32 => "nistp256", 48 => "nistp384", _ => "nistp521" };
                blob.String("ecdsa-sha2-" + curve).String(curve).String([4, .. parameters.Q.X, .. parameters.Q.Y!]);
                SignatureAlgorithms = ["ecdsa-sha2-" + curve];
                break;
            }
            case RSA rsa:
            {
                var parameters = rsa.ExportParameters(false);
                blob.String("ssh-rsa").MPInt(parameters.Exponent).MPInt(parameters.Modulus);
                SignatureAlgorithms = ["rsa-sha2-512", "rsa-sha2-256"];
                break;
            }
            default:
                throw new ArgumentException("Only ECDSA and RSA keys are supported.", nameof(key));
        }
        PublicKey = SshPublicKey.FromBlob(blob.Span, comment);
    }

    /// <summary>A new ECDSA key on NIST P-256 (or P-384 or P-521 for <paramref name="bits"/> 384 or 521).</summary>
    public static SshKeyPair CreateEcdsa(int bits = 256, string? comment = "intertube-stub") => new(ECDsa.Create(bits switch
    {
        256 => ECCurve.NamedCurves.nistP256,
        384 => ECCurve.NamedCurves.nistP384,
        521 => ECCurve.NamedCurves.nistP521,
        _ => throw new ArgumentOutOfRangeException(nameof(bits), "Use 256, 384 or 521."),
    }), comment);

    /// <summary>A new RSA key.</summary>
    public static SshKeyPair CreateRsa(int bits = 3072, string? comment = "intertube-stub") => new(RSA.Create(bits), comment);

    /// <summary>Wrap an existing key. The pair takes ownership of it.</summary>
    public static SshKeyPair FromKey(AsymmetricAlgorithm key, string? comment = null) => new(key, comment);

    /// <summary>The public half, for <c>authorized_keys</c> or <c>known_hosts</c>.</summary>
    public SshPublicKey PublicKey { get; }

    /// <summary>The private key as PEM: <c>EC PRIVATE KEY</c> (SEC 1) or <c>RSA PRIVATE KEY</c> (PKCS #1).</summary>
    public string PrivateKeyPem => _key switch
    {
        ECDsa ecdsa => ecdsa.ExportECPrivateKeyPem(),
        RSA rsa => rsa.ExportRSAPrivateKeyPem(),
        _ => throw new InvalidOperationException(),
    };

    internal string[] SignatureAlgorithms { get; }

    /// <summary>
    /// Write <see cref="PrivateKeyPem"/> to <paramref name="path"/>, readable only by the current user
    /// on Unix (OpenSSH refuses private keys others can read).
    /// </summary>
    public void WritePrivateKey(string path)
    {
        File.WriteAllText(path, PrivateKeyPem.ReplaceLineEndings("\n") + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>Sign <paramref name="data"/>, returning an SSH signature blob.</summary>
    internal byte[] Sign(string algorithm, ReadOnlySpan<byte> data)
    {
        var blob = new SshWriter().String(algorithm);
        switch (_key)
        {
            case ECDsa ecdsa:
            {
                var size = ecdsa.KeySize switch { 256 => 32, 384 => 48, _ => 66 };
                var hash = size switch { 32 => HashAlgorithmName.SHA256, 48 => HashAlgorithmName.SHA384, _ => HashAlgorithmName.SHA512 };
                var p1363 = ecdsa.SignData(data, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                blob.String(new SshWriter().MPInt(p1363.AsSpan(0, size)).MPInt(p1363.AsSpan(size)).Span);
                break;
            }
            case RSA rsa:
            {
                var hash = algorithm == "rsa-sha2-256" ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA512;
                blob.String(rsa.SignData(data.ToArray(), hash, RSASignaturePadding.Pkcs1));
                break;
            }
        }
        return blob.ToArray();
    }

    public void Dispose() => _key.Dispose();
}
