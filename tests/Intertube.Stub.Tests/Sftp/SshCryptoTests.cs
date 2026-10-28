using System.Text;
using Intertube.Stub.Sftp;
using Intertube.Stub.Sftp.Ssh;

namespace Intertube.Stub.Tests.Sftp;

public class SshCryptoTests
{
    /// <summary>Test vectors 1 to 3 of RFC 8032 section 7.1.</summary>
    [Theory]
    [InlineData("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", "",
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b")]
    [InlineData("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c", "72",
        "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00")]
    [InlineData("fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025", "af82",
        "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a")]
    public void Ed25519_AcceptsRfc8032Vectors_AndRejectsAnyFlippedBit(string publicKey, string message, string signature)
    {
        var key = Convert.FromHexString(publicKey);
        var data = Convert.FromHexString(message);
        var sig = Convert.FromHexString(signature);
        Assert.True(Ed25519.Verify(key, data, sig));

        foreach (var bit in new[] { 0, 100, 255, 256, 300, 511 })
        {
            var tampered = (byte[])sig.Clone();
            tampered[bit / 8] ^= (byte)(1 << (bit % 8));
            Assert.False(Ed25519.Verify(key, data, tampered));
        }
        Assert.False(Ed25519.Verify(key, [.. data, 0], sig));
    }

    [Theory]
    [InlineData("ecdsa", 256)]
    [InlineData("ecdsa", 384)]
    [InlineData("ecdsa", 521)]
    [InlineData("rsa", 2048)]
    public void KeyPair_SignaturesVerifyThroughTheParsedPublicKey(string type, int bits)
    {
        using var pair = type == "rsa" ? SshKeyPair.CreateRsa(bits) : SshKeyPair.CreateEcdsa(bits);
        var parsed = SshPublicKey.Parse(pair.PublicKey.ToString());
        var data = Encoding.UTF8.GetBytes("exchange hash");

        foreach (var algorithm in pair.SignatureAlgorithms)
        {
            var signature = pair.Sign(algorithm, data);
            Assert.True(parsed.Verify(data, signature));
            Assert.False(parsed.Verify([.. data, 1], signature));
        }
        Assert.Equal(pair.PublicKey, parsed);
        Assert.StartsWith("SHA256:", parsed.Fingerprint);
    }

    [Fact]
    public void PublicKeyParse_RejectsMismatchedTypeAndGarbage()
    {
        using var pair = SshKeyPair.CreateEcdsa();
        var line = pair.PublicKey.ToString();
        Assert.Throws<FormatException>(() => SshPublicKey.Parse("ssh-ed25519 " + line.Split(' ')[1]));
        Assert.Throws<FormatException>(() => SshPublicKey.Parse("ssh-ed25519 not*base64"));
        Assert.Throws<FormatException>(() => SshPublicKey.Parse("just-one-field"));
    }

    [Theory]
    [InlineData("", "/")]
    [InlineData(".", "/")]
    [InlineData("a/./b//c/", "/a/b/c")]
    [InlineData("/a/../../..", "/")]
    [InlineData("../etc/passwd", "/etc/passwd")]
    [InlineData(@"a\b", "/a/b")]
    public void SftpPath_NormalizesAndNeverEscapesTheRoot(string path, string expected) =>
        Assert.Equal(expected, SftpPath.Normalize(path));
}
