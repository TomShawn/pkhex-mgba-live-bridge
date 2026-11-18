using PKHeX.Core;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

Check(StringConverter3.GetString(Convert.FromHexString("0E2C084CFF000000"), 2) == "小雷", "Trainer decoding");
Check(StringConverter3.GetString(Convert.FromHexString("0E2C084CFF000000"), false) == "小雷", "Trainer bool decoding");
Check(StringConverter3.GetString(Convert.FromHexString("09660550FF0000000000"), 2) == "梦幻", "Mew decoding");
foreach (var value in new[] { "小雷", "梦幻", "水跃鱼", "尼玛", "小A雷", "Mew123", "A♀" })
{
    var bytes = new byte[20];
    StringConverter3.SetString(bytes, value, 20, 2);
    Check(StringConverter3.GetString(bytes, 2) == value, "Roundtrip " + value);
}
var trainer = new byte[8];
StringConverter3.SetString(trainer, "小雷梦幻", 7, 2);
Check(StringConverter3.GetString(trainer, 2) == "小雷梦", "Encoded byte limit");
var nickname = new byte[10];
StringConverter3.SetString(nickname, "妙蛙种子鱼", 10, 2);
Check(StringConverter3.GetString(nickname, 2) == "妙蛙种子鱼", "Full nickname buffer");
int total = 0;
foreach (var line in File.ReadLines(args[0]))
{
    var fields = line.Split('\t');
    var text = fields[1];
    var bytes = new byte[4];
    StringConverter3.SetString(bytes, text, 4, 2);
    Check(StringConverter3.GetString(bytes, 2) == text, "Character roundtrip " + text);
    total++;
}
var original = Enumerable.Repeat((byte)0x11, 10).ToArray();
var unchanged = (byte[])original.Clone();
try
{
    StringConverter3.SetString(original, "🙂", 10, 2);
    throw new Exception("Unsupported input must fail");
}
catch (ArgumentException)
{
    Check(original.SequenceEqual(unchanged), "Rejected input must preserve buffer");
}
Console.WriteLine($"PASS {total} mapped characters, trainer/nickname/mixed text, byte limits and rejection atomicity.");
if (args.Length > 1)
{
    var save = new SAV3E(File.ReadAllBytes(args[1]));
    Console.WriteLine($"Loaded save {save.GetType().Name}, trainer {save.OT}.");
    for (int slot = 0; slot < save.PartyCount; slot++)
    {
        var pokemon = save.GetPartySlotAtIndex(slot);
        Console.WriteLine($"Party {slot + 1}: {pokemon.Species}, {pokemon.Nickname}, OT {pokemon.OriginalTrainerName}.");
    }
}
