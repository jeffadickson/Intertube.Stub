using System.Numerics;
using System.Security.Cryptography;

namespace Intertube.Stub.Sftp.Ssh;

/// <summary>
/// Ed25519 signature verification (RFC 8032 section 5.1.7), which .NET does not provide. It uses
/// BigInteger arithmetic: slow by crypto standards (a few milliseconds per check) and not constant
/// time, which is fine for verifying a test client's login and nothing else.
/// </summary>
internal static class Ed25519
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(-121665 * Inverse(121666));
    private static readonly BigInteger SqrtMinusOne = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly Point BasePoint = Decode(Convert.FromHexString("5866666666666666666666666666666666666666666666666666666666666666"))!.Value;

    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64) return false;
        if (Decode(signature[..32]) is not { } r || Decode(publicKey) is not { } a) return false;
        var s = new BigInteger(signature[32..], isUnsigned: true);
        if (s >= L) return false;

        var hash = new byte[64];
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512))
        {
            sha.AppendData(signature[..32]);
            sha.AppendData(publicKey);
            sha.AppendData(message);
            sha.GetHashAndReset(hash);
        }
        var k = new BigInteger(hash, isUnsigned: true) % L;
        return Equal(Multiply(BasePoint, s), Add(r, Multiply(a, k)));
    }

    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    private static Point? Decode(ReadOnlySpan<byte> encoded)
    {
        Span<byte> bytes = stackalloc byte[32];
        encoded.CopyTo(bytes);
        var sign = bytes[31] >> 7;
        bytes[31] &= 0x7F;
        var y = new BigInteger(bytes, isUnsigned: true);
        if (y >= P) return null;

        var y2 = y * y % P;
        var x2 = Mod((y2 - 1) * Inverse(D * y2 + 1));
        if (x2.IsZero)
        {
            if (sign == 1) return null;
            return new Point(0, y, 1, 0);
        }
        var x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (Mod(x * x - x2) != 0) x = x * SqrtMinusOne % P;
        if (Mod(x * x - x2) != 0) return null;
        if ((int)(x % 2) != sign) x = P - x;
        return new Point(x, y, 1, x * y % P);
    }

    private static Point Add(Point p, Point q)
    {
        var a = Mod((p.Y - p.X) * (q.Y - q.X));
        var b = (p.Y + p.X) * (q.Y + q.X) % P;
        var c = 2 * D * p.T % P * q.T % P;
        var d = 2 * p.Z * q.Z % P;
        var e = Mod(b - a);
        var f = Mod(d - c);
        var g = (d + c) % P;
        var h = (b + a) % P;
        return new Point(e * f % P, g * h % P, f * g % P, e * h % P);
    }

    private static Point Multiply(Point point, BigInteger scalar)
    {
        var result = new Point(0, 1, 1, 0);
        for (var bit = (int)scalar.GetBitLength() - 1; bit >= 0; bit--)
        {
            result = Add(result, result);
            if (!(scalar >> bit).IsEven) result = Add(result, point);
        }
        return result;
    }

    private static bool Equal(Point p, Point q) =>
        Mod(p.X * q.Z - q.X * p.Z) == 0 && Mod(p.Y * q.Z - q.Y * p.Z) == 0;

    private static BigInteger Inverse(BigInteger value) => BigInteger.ModPow(Mod(value), P - 2, P);

    private static BigInteger Mod(BigInteger value)
    {
        var r = value % P;
        return r.Sign < 0 ? r + P : r;
    }
}
