using System.Globalization;
using System.Reflection;
using PKHeX.Core;

namespace PKHeX.ChineseCompat;

/// <summary>Double-byte character encoding used by the 2011 Chinese Emerald translation.</summary>
public static class ChineseCodec
{
    private static readonly Dictionary<ushort, char> Characters = ReadMap();
    private static readonly Dictionary<char, ushort> Codes = Characters
        .GroupBy(pair => pair.Value)
        .ToDictionary(group => group.Key, group => group.First().Key);

    private static Dictionary<ushort, char> ReadMap()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ChineseMap.tsv")!;
        using var reader = new StreamReader(stream);
        var map = new Dictionary<ushort, char>();
        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split('\t');
            map.Add(ushort.Parse(fields[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture), fields[1][0]);
        }
        return map;
    }

    public static int LoadString(ReadOnlySpan<byte> data, Span<char> result, int language)
    {
        int input = 0;
        int output = 0;
        while (input < data.Length && data[input] != StringConverter3.TerminatorByte)
        {
            ushort code = input + 1 < data.Length ? (ushort)((data[input] << 8) | data[input + 1]) : (ushort)0;
            if (Characters.TryGetValue(code, out char chinese))
            {
                result[output++] = chinese;
                input += 2;
                continue;
            }
            char character = StringConverter3.GetG3Char(data[input++], language);
            if (character == (char)StringConverter3.TerminatorByte)
                break;
            result[output++] = StringConverter4Util.NormalizeGenderSymbol(character);
        }
        return output;
    }

    public static int SetString(Span<byte> buffer, ReadOnlySpan<char> value, int maxLength,
        int language, StringConverterOption option = StringConverterOption.ClearFF)
    {
        int limit = Math.Min(buffer.Length, maxLength);
        Span<byte> encoded = stackalloc byte[buffer.Length];
        int written = 0;
        foreach (char character in value)
        {
            if (written >= limit)
                break;
            if (Codes.TryGetValue(character, out ushort code))
            {
                if (written + 2 > limit)
                    break;
                encoded[written++] = (byte)(code >> 8);
                encoded[written++] = (byte)code;
                continue;
            }
            char normalized = StringConverter4Util.UnNormalizeGenderSymbol(character);
            byte singleByte = StringConverter3.SetG3Char(normalized, language);
            if (singleByte == StringConverter3.TerminatorByte)
                throw new ArgumentException($"当前 GBA 汉化字库不支持字符“{character}”。");
            encoded[written++] = singleByte;
        }
        if (option == StringConverterOption.ClearFF)
            buffer.Fill(StringConverter3.TerminatorByte);
        else if (option == StringConverterOption.ClearZero)
            buffer.Clear();
        encoded[..written].CopyTo(buffer);
        if (written < buffer.Length)
            buffer[written++] = StringConverter3.TerminatorByte;
        return written;
    }
}
