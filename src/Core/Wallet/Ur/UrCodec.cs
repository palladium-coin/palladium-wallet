namespace PalladiumWallet.Core.Wallet.Ur;

/// <summary>
/// Produces the frames of an animated QR (Blockchain Commons UR, BCR-2020-005/-006) for a payload: a PSBT for an
/// air-gapped signer (Keystone, Passport, Jade, SeedSigner, Sparrow...). A short payload is one frame; a longer one is an
/// endless cycle of parts, so the reader may start anywhere and miss frames.
/// </summary>
public sealed class UrEncoder
{
    private readonly string _type;
    private readonly byte[] _message;
    private readonly byte[][] _fragments;
    private readonly uint _checksum;
    private uint _seqNum;

    /// <param name="type">UR type, e.g. <c>crypto-psbt</c>.</param>
    /// <param name="cborPayload">The CBOR of the UR value (for a PSBT: a byte string).</param>
    /// <param name="maxFragmentLength">Largest fragment: bigger means denser QR codes and fewer frames.</param>
    public UrEncoder(string type, byte[] cborPayload, int maxFragmentLength = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFragmentLength, 1);
        ArgumentOutOfRangeException.ThrowIfZero(cborPayload.Length);
        _type = type;
        _message = cborPayload;
        _checksum = Fountain.Checksum(cborPayload);
        _fragments = Fountain.Split(cborPayload, Fountain.FragmentLength(cborPayload.Length, maxFragmentLength));
        IsSinglePart = cborPayload.Length <= maxFragmentLength;
    }

    public bool IsSinglePart { get; }

    public int PartCount => _fragments.Length;

    /// <summary>A PSBT as the frames of a <c>crypto-psbt</c> UR.</summary>
    public static UrEncoder ForPsbt(byte[] psbt, int maxFragmentLength = 100)
    {
        var cbor = new List<byte>();
        Cbor.WriteBytes(cbor, psbt);
        return new UrEncoder(UrDecoder.PsbtType, cbor.ToArray(), maxFragmentLength);
    }

    /// <summary>The next frame (lowercase; uppercase it for the QR alphanumeric mode, which packs denser).</summary>
    public string NextPart()
    {
        if (IsSinglePart)
            return $"ur:{_type}/{Bytewords.EncodeMinimal(_message)}";
        _seqNum++;
        var indexes = Fountain.ChooseFragments(_seqNum, _fragments.Length, _checksum);
        var data = (byte[])_fragments[indexes[0]].Clone();
        foreach (var i in indexes.Skip(1))
            Fountain.Xor(data, _fragments[i]);
        var o = new List<byte>();
        Cbor.WriteArrayHead(o, 5);
        Cbor.WriteUInt(o, _seqNum);
        Cbor.WriteUInt(o, (ulong)_fragments.Length);
        Cbor.WriteUInt(o, (ulong)_message.Length);
        Cbor.WriteUInt(o, _checksum);
        Cbor.WriteBytes(o, data);
        return $"ur:{_type}/{_seqNum}-{_fragments.Length}/{Bytewords.EncodeMinimal(o.ToArray())}";
    }
}

/// <summary>
/// Rebuilds a message from scanned UR frames, in any order, with repeats and gaps. Everything it reads comes from a QR
/// code someone else displayed, so sizes are bounded and every inconsistency makes <see cref="Receive"/> refuse the
/// frame instead of throwing; the rebuilt message is accepted only if its CRC-32 matches.
/// </summary>
public sealed class UrDecoder
{
    public const string PsbtType = "crypto-psbt";

    private const int MaxMessageLength = 4 * 1024 * 1024;
    private const int MaxParts = 20_000;

    private string? _type;
    private int _seqLen;
    private int _messageLen;
    private uint _checksum;
    private int _fragmentLen;
    private byte[]?[] _known = [];
    private int _knownCount;
    private readonly HashSet<uint> _seen = [];
    private readonly List<(HashSet<int> Indexes, byte[] Data)> _mixed = [];
    private byte[]? _result;

    public bool IsComplete => _result is not null;

    /// <summary>UR type of the frames seen so far (null before the first valid frame).</summary>
    public string? Type => _type;

    /// <summary>0..1: fragments recovered over fragments needed.</summary>
    public double Progress => IsComplete ? 1 : _seqLen == 0 ? 0 : (double)_knownCount / _seqLen;

    /// <summary>The CBOR payload once <see cref="IsComplete"/>.</summary>
    public byte[]? Message => _result;

    /// <summary>The PSBT bytes when the completed message is a PSBT UR, else null.</summary>
    public byte[]? Psbt
    {
        get
        {
            if (_result is null || (_type != PsbtType && _type != "psbt"))
                return null;
            try
            {
                var r = new Cbor.Reader(_result);
                var b = r.ReadBytes();
                return r.AtEnd ? b : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }

    /// <summary>Feeds one scanned frame; false if it is not a valid frame of the message being collected.</summary>
    public bool Receive(string frame)
    {
        if (IsComplete)
            return false;
        try
        {
            return ReceiveCore(frame.Trim());
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            return false;
        }
    }

    private bool ReceiveCore(string frame)
    {
        var lower = frame.ToLowerInvariant();
        if (!lower.StartsWith("ur:", StringComparison.Ordinal))
            return false;
        var comps = lower[3..].Split('/');
        if (comps.Length is < 2 or > 3 || comps[0].Length == 0 || comps[0].Length > 64)
            return false;
        var type = comps[0];
        if (_type is not null && _type != type)
            return false;

        if (comps.Length == 2)   // single part: the whole message
        {
            var msg = Bytewords.DecodeMinimal(comps[1]);
            _type = type;
            _result = msg;
            return true;
        }

        var seq = comps[1].Split('-');
        if (seq.Length != 2 || !uint.TryParse(seq[0], out var seqNumHdr) || !int.TryParse(seq[1], out var seqLenHdr)
            || seqNumHdr == 0 || seqLenHdr < 1)
            return false;

        var r = new Cbor.Reader(Bytewords.DecodeMinimal(comps[2]));
        if (r.ReadArrayHead() != 5)
            return false;
        var seqNum = r.ReadUInt();
        var seqLen = r.ReadUInt();
        var messageLen = r.ReadUInt();
        var checksum = r.ReadUInt();
        var data = r.ReadBytes();
        if (!r.AtEnd || seqNum != seqNumHdr || seqLen != (ulong)seqLenHdr || checksum > uint.MaxValue || seqNum > uint.MaxValue)
            return false;
        if (seqLen is < 1 or > MaxParts || messageLen is < 1 or > MaxMessageLength || data.Length == 0)
            return false;
        // The sender's split: ceil(messageLen / seqLen) per fragment, and no fragment left empty.
        var fragLen = data.Length;
        if ((ulong)fragLen * seqLen < messageLen || (ulong)fragLen * (seqLen - 1) >= messageLen)
            return false;

        if (_seqLen == 0)
        {
            _type = type;
            _seqLen = (int)seqLen;
            _messageLen = (int)messageLen;
            _checksum = (uint)checksum;
            _fragmentLen = fragLen;
            _known = new byte[_seqLen][];
        }
        else if (_seqLen != (int)seqLen || _messageLen != (int)messageLen || _checksum != (uint)checksum || _fragmentLen != fragLen)
        {
            return false;   // a frame of another message
        }

        if (!_seen.Add((uint)seqNum))
            return true;    // a repeat: harmless
        var indexes = Fountain.ChooseFragments((uint)seqNum, _seqLen, _checksum).ToHashSet();
        AddPart(indexes, data);
        TryFinish();
        return true;
    }

    private void AddPart(HashSet<int> indexes, byte[] data)
    {
        var d = (byte[])data.Clone();
        foreach (var i in indexes.Where(i => _known[i] is not null).ToList())
        {
            Fountain.Xor(d, _known[i]!);
            indexes.Remove(i);
        }
        if (indexes.Count == 0)
            return;
        if (indexes.Count > 1)
        {
            _mixed.Add((indexes, d));
            return;
        }
        // One fragment left: it is known; knowing it may reduce the stored mixes to more of them.
        var work = new Queue<(int Index, byte[] Data)>();
        work.Enqueue((indexes.First(), d));
        while (work.Count > 0)
        {
            var (idx, frag) = work.Dequeue();
            if (_known[idx] is not null)
                continue;
            _known[idx] = frag;
            _knownCount++;
            for (var m = _mixed.Count - 1; m >= 0; m--)
            {
                var (set, mixData) = _mixed[m];
                if (!set.Remove(idx))
                    continue;
                Fountain.Xor(mixData, frag);
                if (set.Count == 1)
                {
                    work.Enqueue((set.First(), mixData));
                    _mixed.RemoveAt(m);
                }
            }
        }
    }

    private void TryFinish()
    {
        if (_knownCount != _seqLen)
            return;
        var all = new byte[_seqLen * _fragmentLen];
        for (var i = 0; i < _seqLen; i++)
            Array.Copy(_known[i]!, 0, all, i * _fragmentLen, _fragmentLen);
        var message = all.AsSpan(0, _messageLen).ToArray();
        if (Crc32.Compute(message) == _checksum)
            _result = message;
        // A mismatch means a corrupted or forged frame slipped in: restart the collection rather than keep it.
        else
            Reset();
    }

    private void Reset()
    {
        _seqLen = 0;
        _knownCount = 0;
        _known = [];
        _seen.Clear();
        _mixed.Clear();
    }
}
