namespace PalladiumWallet.Core.Wallet.Ur;

/// <summary>
/// The tiny slice of CBOR (RFC 8949) a UR needs: unsigned integers, byte strings and definite-length arrays, always in
/// the shortest form. Anything else is rejected, so a hostile QR cannot make the reader allocate or loop.
/// </summary>
internal static class Cbor
{
    public static void WriteUInt(List<byte> o, ulong v) => WriteHead(o, 0, v);

    public static void WriteBytes(List<byte> o, ReadOnlySpan<byte> b)
    {
        WriteHead(o, 2, (ulong)b.Length);
        foreach (var x in b)
            o.Add(x);
    }

    public static void WriteArrayHead(List<byte> o, int count) => WriteHead(o, 4, (ulong)count);

    private static void WriteHead(List<byte> o, int major, ulong v)
    {
        var m = (byte)(major << 5);
        if (v < 24) o.Add((byte)(m | (byte)v));
        else if (v <= 0xFF) { o.Add((byte)(m | 24)); o.Add((byte)v); }
        else if (v <= 0xFFFF) { o.Add((byte)(m | 25)); o.Add((byte)(v >> 8)); o.Add((byte)v); }
        else if (v <= 0xFFFFFFFF) { o.Add((byte)(m | 26)); for (var s = 24; s >= 0; s -= 8) o.Add((byte)(v >> s)); }
        else { o.Add((byte)(m | 27)); for (var s = 56; s >= 0; s -= 8) o.Add((byte)(v >> s)); }
    }

    public sealed class Reader(byte[] data)
    {
        private int _pos;

        public bool AtEnd => _pos == data.Length;

        public ulong ReadUInt() => ReadHead(0);

        public int ReadArrayHead() => checked((int)ReadHead(4));

        public byte[] ReadBytes()
        {
            var len = ReadHead(2);
            if (len > (ulong)(data.Length - _pos))
                throw new FormatException("CBOR: byte string overruns the data.");
            var r = data.AsSpan(_pos, (int)len).ToArray();
            _pos += (int)len;
            return r;
        }

        private ulong ReadHead(int expectedMajor)
        {
            if (_pos >= data.Length)
                throw new FormatException("CBOR: truncated.");
            var b = data[_pos++];
            if (b >> 5 != expectedMajor)
                throw new FormatException("CBOR: unexpected type.");
            var info = b & 0x1F;
            if (info < 24)
                return (ulong)info;
            var n = info switch { 24 => 1, 25 => 2, 26 => 4, 27 => 8, _ => throw new FormatException("CBOR: unsupported length.") };
            if (data.Length - _pos < n)
                throw new FormatException("CBOR: truncated.");
            ulong v = 0;
            for (var i = 0; i < n; i++)
                v = v << 8 | data[_pos++];
            return v;
        }
    }
}
