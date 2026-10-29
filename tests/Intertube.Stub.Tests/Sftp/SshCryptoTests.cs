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
            Assert.True(parsed.Verify(data, signature, algorithm));
            Assert.False(parsed.Verify([.. data, 1], signature, algorithm));
            Assert.False(parsed.Verify(data, [.. signature, 0], algorithm));
            foreach (var other in new[] { "ssh-rsa", "rsa-sha2-256", "rsa-sha2-512", "ssh-ed25519", "ecdsa-sha2-nistp256" }.Where(a => a != algorithm))
                Assert.False(parsed.Verify(data, signature, other));
        }
        Assert.Equal(pair.PublicKey, parsed);
        Assert.StartsWith("SHA256:", parsed.Fingerprint);
    }

    [Theory]
    [InlineData("ffffffff")]
    [InlineData("80000000")]
    [InlineData("00000005aabb")]
    public void SshReader_StringLongerThanTheMessage_IsAProtocolError(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        Assert.Throws<SshProtocolException>(() => new SshReader(bytes).String().Length);
    }

    [Theory]
    [InlineData("00000000")] // zero
    [InlineData("0000000180")] // negative
    [InlineData("000000020001")] // needless leading zero
    [InlineData("00000002007f")]
    [InlineData("00000003010203")] // too long for 2 bytes
    public void UnsignedFixed_RejectsAnythingButAMinimalPositiveMpint(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        Assert.Throws<SshProtocolException>(() => new SshReader(bytes).UnsignedFixed(2));
    }

    [Fact]
    public void UnsignedFixed_AcceptsMinimalEncodings()
    {
        Assert.Equal(new byte[] { 0, 0x7f }, new SshReader(Convert.FromHexString("000000017f")).UnsignedFixed(2));
        Assert.Equal(new byte[] { 0x80, 1 }, new SshReader(Convert.FromHexString("00000003008001")).UnsignedFixed(2));
    }

    [Fact]
    public void EcdsaVerify_RejectsReencodedOrPaddedSignatures()
    {
        using var pair = SshKeyPair.CreateEcdsa();
        var algorithm = pair.SignatureAlgorithms[0];
        var data = Encoding.UTF8.GetBytes("exchange hash");
        byte[] signature;
        do signature = pair.Sign(algorithm, data);
        while (SignatureParts(signature).R[0] == 0); // a minimal r whose top bit is clear, so a padded copy is non-minimal

        var (r, s) = SignatureParts(signature);
        Assert.True(pair.PublicKey.Verify(data, signature, algorithm));
        Assert.False(pair.PublicKey.Verify(data, Signature(algorithm, new SshWriter().String([0, .. r]).String(s)), algorithm));
        Assert.False(pair.PublicKey.Verify(data, Signature(algorithm, new SshWriter().String(r).String(s).Byte(0)), algorithm));
    }

    private static (byte[] R, byte[] S) SignatureParts(byte[] signature)
    {
        var outer = new SshReader(signature);
        outer.Utf8();
        var inner = new SshReader(outer.String());
        return (inner.String().ToArray(), inner.String().ToArray());
    }

    private static byte[] Signature(string algorithm, SshWriter rs) => new SshWriter().String(algorithm).String(rs.Span).ToArray();

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
