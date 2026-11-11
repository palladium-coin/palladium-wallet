namespace PalladiumWallet.Core.Wallet.Ur;

/// <summary>
/// Bytewords (BCR-2020-012), the text form of a UR: every byte becomes one of 256 four-letter words and a CRC-32 of the
/// data is appended before encoding. The "minimal" style keeps only the first and last letter of each word, 2 characters
/// per byte, which is what QR codes carry.
/// </summary>
public static class Bytewords
{
    private const string Words =
        "able acid also apex aqua arch atom aunt away axis back bald barn belt beta bias blue body brag brew bulb buzz calm cash cats chef city claw code cola cook cost crux curl cusp cyan " +
        "dark data days deli dice diet door down draw drop drum dull duty each easy echo edge epic even exam exit eyes fact fair fern figs film fish fizz flap flew flux foxy free frog fuel fund " +
        "gala game gear gems gift girl glow good gray grim guru gush gyro half hang hard hawk heat help high hill holy hope horn huts iced idea idle inch inky into iris iron item jade jazz join " +
        "jolt jowl judo jugs jump junk jury keep keno kept keys kick kiln king kite kiwi knob lamb lava lazy leaf legs liar limp lion list logo loud love luau luck lung main many math maze memo " +
        "menu meow mild mint miss monk nail navy need news next noon note numb obey oboe omit onyx open oval owls paid part peck play plus poem pool pose puff puma purr quad quiz race ramp real " +
        "redo rich road rock roof ruby ruin runs rust safe saga scar sets silk skew slot soap solo song stub surf swan taco task taxi tent tied time tiny toil tomb toys trip tuna twin ugly undo " +
        "unit urge user vast very veto vial vibe view visa void vows wall wand warm wasp wave waxy webs what when whiz wolf work yank yawn yell yoga yurt zaps zero zest zinc zone zoom";

    internal static readonly string[] WordList = Words.Split(' ');

    // first letter + last letter -> byte value
    private static readonly Dictionary<(char, char), byte> MinimalIndex = BuildIndex();

    private static Dictionary<(char, char), byte> BuildIndex()
    {
        var map = new Dictionary<(char, char), byte>(256);
        for (var i = 0; i < WordList.Length; i++)
            map[(WordList[i][0], WordList[i][3])] = (byte)i;
        return map;
    }

    /// <summary>Encodes <paramref name="data"/> followed by its CRC-32, in the minimal style (lowercase).</summary>
    public static string EncodeMinimal(ReadOnlySpan<byte> data)
    {
        var crc = Crc32.Compute(data);
        var sb = new System.Text.StringBuilder((data.Length + 4) * 2);
        foreach (var b in data)
            Append(sb, b);
        for (var shift = 24; shift >= 0; shift -= 8)
            Append(sb, (byte)(crc >> shift));
        return sb.ToString();

        static void Append(System.Text.StringBuilder sb, byte b)
        {
            var w = WordList[b];
            sb.Append(w[0]).Append(w[3]);
        }
    }

    /// <summary>Decodes minimal bytewords and checks the CRC-32; <see cref="FormatException"/> if the text is not valid.</summary>
    public static byte[] DecodeMinimal(string text)
    {
        if (text.Length % 2 != 0 || text.Length < 10)
            throw new FormatException("Bytewords: bad length.");
        var raw = new byte[text.Length / 2];
        for (var i = 0; i < raw.Length; i++)
        {
            if (!MinimalIndex.TryGetValue((char.ToLowerInvariant(text[2 * i]), char.ToLowerInvariant(text[2 * i + 1])), out raw[i]))
                throw new FormatException("Bytewords: unknown word.");
        }
        var body = raw.AsSpan(0, raw.Length - 4);
        var crc = (uint)(raw[^4] << 24 | raw[^3] << 16 | raw[^2] << 8 | raw[^1]);
        if (Crc32.Compute(body) != crc)
            throw new FormatException("Bytewords: checksum mismatch.");
        return body.ToArray();
    }
}

/// <summary>CRC-32 (IEEE 802.3, the zlib one) that UR uses to protect its payloads.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
