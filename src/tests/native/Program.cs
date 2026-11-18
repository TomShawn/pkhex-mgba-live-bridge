using PKHeX.GbaBridge;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static byte[] BuildSyntheticSave()
{
    byte[] small = new byte[0xF2C];
    Convert.FromHexString("0E2C084CFF000000").CopyTo(small, 0);
    Convert.FromHexString("965BFC2D").CopyTo(small, 10);
    byte[] large = new byte[0x3D88];
    large[0x234] = 4;
    uint[] pids = [0x5587BF16u, 0x656ED628u, 0xDFFD7B88u, 0x91FD6448u];
    for (int slot = 0; slot < pids.Length; slot++)
    {
        byte[] mon = new byte[100];
        SaveCodec.Put32(mon, 0, pids[slot]);
        SaveCodec.Put32(mon, 4, 0x2DFC5B96u);
        Convert.FromHexString("09660550FF0000000000").CopyTo(mon, 8);
        mon[18] = 2;
        mon[19] = 2;
        Convert.FromHexString("0E2C084CFF0000").CopyTo(mon, 20);
        SaveCodec.Put16(mon, 32, 151);
        SaveCodec.Put32(mon, 36, 1000000);
        SaveCodec.Put16(mon, 44, 85);
        SaveCodec.Put16(mon, 46, 100);
        SaveCodec.Put16(mon, 48, 101);
        SaveCodec.Put16(mon, 50, 102);
        mon[52] = 10; mon[53] = 15; mon[54] = 20; mon[55] = 25;
        mon[84] = 100;
        SaveCodec.Put16(mon, 86, 500);
        SaveCodec.Put16(mon, 88, 500);
        for (int stat = 90; stat < 100; stat += 2) SaveCodec.Put16(mon, stat, 300);
        SaveCodec.EncodeMon(mon).CopyTo(large, 0x238 + slot * 100);
    }
    SaveCodec.Put32(large, 0x490, 3000);
    SaveCodec.Put16(large, 0x650, 1);
    SaveCodec.Put16(large, 0x652, 99);
    byte[] boxes = new byte[0x83D0];
    SaveData encoded = SaveCodec.EncodeWithKey(new SaveData(small, large, boxes, 1), 0x12345678);
    byte[] image = new byte[0x20010];
    for (int section = 0; section < 14; section++)
    {
        int offset = section * 4096;
        ReadOnlySpan<byte> payload = section == 0 ? encoded.Small
            : section < 5 ? encoded.Large.AsSpan((section - 1) * 0xF80, SaveCodec.Sizes[section])
            : encoded.Boxes.AsSpan((section - 5) * 0xF80, SaveCodec.Sizes[section]);
        payload.CopyTo(image.AsSpan(offset));
        SaveCodec.Put16(image, offset + 0xFF4, (ushort)section);
        SaveCodec.Put16(image, offset + 0xFF6, SaveCodec.Checksum(payload.ToArray()));
        SaveCodec.Put32(image, offset + 0xFF8, 0x08012025);
        SaveCodec.Put32(image, offset + 0xFFC, 1);
    }
    return image;
}

string saveFixturePath;
string? temporarySaveFixture = null;
if (args.Length > 0 && File.Exists(args[0]))
{
    saveFixturePath = args[0];
}
else
{
    temporarySaveFixture = Path.Combine(Path.GetTempPath(), "gba-synthetic-save-" + Guid.NewGuid().ToString("N") + ".sav");
    File.WriteAllBytes(temporarySaveFixture, BuildSyntheticSave());
    saveFixturePath = temporarySaveFixture;
}

try
{
var saved = SaveCodec.Parse(File.ReadAllBytes(saveFixturePath));
var plain = SaveCodec.Canonical(saved);
foreach (uint key in new uint[] { 0, 1, uint.MaxValue, 0x89ABCDEF })
{
    var result = SaveCodec.Canonical(SaveCodec.EncodeWithKey(plain,key));
    Check(result.Small.SequenceEqual(plain.Small) && result.Large.SequenceEqual(plain.Large),"Encryption roundtrip");
}
Snapshot Live(SaveData value)
{
    var encoded = SaveCodec.EncodeWithKey(value,0x89ABCDEF);
    return new Snapshot(5000,0x02025000,0x02035000,0x02029000,encoded,[value.Large[0x234],..value.Large[0x238..0x490]]);
}
(SaveData Save,byte[] Party) Apply(Snapshot snapshot,List<Patch> patches)
{
    var regions = new[] { (snapshot.SmallAddress,(byte[])snapshot.Raw.Small.Clone()),(snapshot.LargeAddress,(byte[])snapshot.Raw.Large.Clone()),
        (snapshot.BoxAddress,(byte[])snapshot.Raw.Boxes.Clone()),(SaveCodec.PartyAddress,snapshot.Party[1..]),(SaveCodec.PartyCountAddress,snapshot.Party[..1]) };
    foreach (var patch in patches)
    {
        var (address,data) = regions.Single(region => patch.Address >= region.Item1 && patch.Address + patch.Before.Length <= region.Item1 + region.Item2.Length);
        int offset = (int)(patch.Address-address);
        Check(data.AsSpan(offset,patch.Before.Length).SequenceEqual(patch.Before),"Patch precondition");
        patch.After.CopyTo(data,offset);
    }
    return (new SaveData(regions[0].Item2,regions[1].Item2,regions[2].Item2),regions[3].Item2);
}

var changed = (byte[])plain.Large.Clone();
SaveCodec.Put16(changed,0x652,88);
var updated = SaveCodec.EncodeWithKey(new SaveData(plain.Small,changed,plain.Boxes),1234);
var progress = (byte[])plain.Large.Clone();
SaveCodec.Put16(progress,0,34); SaveCodec.Put16(progress,2,51); SaveCodec.Put16(progress,0x238+186,456);
var snapshot = Live(new SaveData(plain.Small,progress,plain.Boxes));
var result1 = Apply(snapshot,SaveCodec.Plan(saved,updated,snapshot));
var normalized = SaveCodec.Canonical(result1.Save);
Check(SaveCodec.U16(normalized.Large,0x652)==88,"Bag quantity");
Check(SaveCodec.U32(result1.Save.Small,0xAC)==0x89ABCDEF,"Live key preservation");
Check(SaveCodec.U16(normalized.Large,0)==34 && SaveCodec.U16(result1.Party,186)==456,"Unedited progress preservation");

int slotOffset = 0x238+100;
var edited = SaveCodec.DecodeMon(plain.Large[slotOffset..(slotOffset+100)]);
SaveCodec.Put16(edited,44,94);edited[52]=10;
changed=(byte[])plain.Large.Clone();SaveCodec.EncodeMon(edited).CopyTo(changed,slotOffset);
updated=SaveCodec.EncodeWithKey(new SaveData(plain.Small,changed,plain.Boxes),1234);
var result2=Apply(snapshot,SaveCodec.Plan(saved,updated,snapshot));
var pokemon=SaveCodec.DecodeMon(result2.Party[100..200]);
Check(SaveCodec.U16(pokemon,44)==94 && pokemon[52]==10 && SaveCodec.U16(pokemon,86)==456,"Pokemon field merge");

changed=(byte[])plain.Large.Clone();changed[0x1280]^=1;
progress=(byte[])plain.Large.Clone();progress[0x1280]^=2;
snapshot=Live(new SaveData(plain.Small,progress,plain.Boxes));
updated=SaveCodec.EncodeWithKey(new SaveData(plain.Small,changed,plain.Boxes),1234);
var result3=Apply(snapshot,SaveCodec.Plan(saved,updated,snapshot));
Check(SaveCodec.Canonical(result3.Save).Large[0x1280]==(plain.Large[0x1280]^3),"Event flag merge");

var before=plain.Large[0x238..0x490];
byte[] after=[..before[100..200],..before[..100],..before[200..]];
var current=(byte[])before.Clone();SaveCodec.Put16(current,186,456);
var swapped=SaveCodec.MergeSlots(before,after,current,100);
Check(swapped.AsSpan(0,8).SequenceEqual(before.AsSpan(100,8)) && SaveCodec.U16(swapped,86)==456,"Party reorder");

bool rejected=false;
current=before[100..200];current[0]^=1;
try { SaveCodec.MergeMon(before[100..200],SaveCodec.EncodeMon(edited),current); }
catch (BridgeException) { rejected=true; }
Check(rejected,"Conflicting identity rejection");

byte[] partial=File.ReadAllBytes(saveFixturePath);
for(int offset=0;offset<28*4096;offset+=4096)
    if(SaveCodec.U32(partial,offset+0xFF8)!=0x08012025 || SaveCodec.U32(partial,offset+0xFFC)!=saved.Counter)
    {
        SaveCodec.Put16(partial,offset+0xFF4,0);SaveCodec.Put32(partial,offset+0xFF8,0x08012025);SaveCodec.Put32(partial,offset+0xFFC,saved.Counter+1);
        break;
    }
rejected=false;
try { SaveCodec.Parse(partial,true); } catch(BridgeException) { rejected=true; }
Check(rejected,"Partial newer save rejection");
Console.WriteLine("PASS 7 native bridge regression scenarios.");

if(args.Length>1)
{
    var actual=BridgeSession.Crc32(File.ReadAllBytes(args[1]));
    Console.WriteLine("ROM CRC32 "+actual.ToString("x8"));
}

// Regression for partial-byte optimistic checks (1000 -> 1001 vs concurrent 744).
var moneyBefore = (byte[])plain.Large.Clone();
SaveCodec.Put32(moneyBefore,0x490,1000);
var moneyAfter = (byte[])moneyBefore.Clone();
SaveCodec.Put32(moneyAfter,0x490,1001);
var moneyBase = new SaveData(plain.Small,moneyBefore,plain.Boxes);
var moneyTarget = new SaveData(plain.Small,moneyAfter,plain.Boxes);
var moneySnapshot = Live(moneyBase);
var moneyPatches = SaveCodec.Plan(moneyBase,moneyTarget,moneySnapshot);
var moneyPatch = moneyPatches.Single(patch => patch.Address == moneySnapshot.LargeAddress+0x490);
Check(moneyPatch.Before.Length==4,"Whole money field precondition");
var concurrentMoney = (byte[])moneySnapshot.Raw.Large.Clone();
SaveCodec.Put32(concurrentMoney,0x490,744 ^ 0x89ABCDEF);
Check(!concurrentMoney.AsSpan(0x490,4).SequenceEqual(moneyPatch.Before),"Concurrent high byte rejected");
Check(moneyPatches.Any(patch=>patch.Address==moneySnapshot.SmallAddress+0xAC && patch.Before.Length==4),"Live encryption dependency");
var monSnapshot=Live(plain);
var monPatches=SaveCodec.Plan(saved,updated,monSnapshot);
// The earlier fixture is an event edit. Use the explicit move edit for the record test.
var monLarge=(byte[])plain.Large.Clone();
SaveCodec.EncodeMon(edited).CopyTo(monLarge,slotOffset);
monPatches=SaveCodec.Plan(plain,new SaveData(plain.Small,monLarge,plain.Boxes),monSnapshot);
Check(monPatches.Any(patch=>patch.Address==SaveCodec.PartyAddress+100 && patch.Before.Length==100),"Whole Pokemon record guard");
rejected=false;
try { SaveCodec.ValidateEditVersion(saved with { Counter=101 },saved with { Counter=100 }); }
catch(BridgeException) { rejected=true; }
Check(rejected,"Old editor counter rejected before planning");
rejected=false;
try { SaveCodec.ValidateEditVersion(saved with { Counter=0 },saved with { Counter=uint.MaxValue }); }
catch(BridgeException) { rejected=true; }
Check(rejected,"Stale version across counter wraparound");

var unknownSmall = (byte[])plain.Small.Clone();
unknownSmall[0x20] ^= 1;
var unknownSnapshot = Live(plain);
var unknownPatches = SaveCodec.Plan(plain,new SaveData(unknownSmall,plain.Large,plain.Boxes),unknownSnapshot);
Check(unknownPatches.Any(patch => patch.Address <= unknownSnapshot.SmallAddress + 0x120
    && patch.Address + patch.Before.Length > unknownSnapshot.SmallAddress + 0x120),"Unknown field crossing chunk boundary guarded");

// Real TCP transport drops a COMMIT reply and simulates an immediate game spend.
var listener=new TcpListener(IPAddress.Loopback,0);
listener.Start();
int port=((IPEndPoint)listener.LocalEndpoint).Port;
int money=1000,commits=0,statusQueries=0;
string? receipt=null;
var pendingEdit=new PendingEdit([1,2,3],saved,"test_session",3);
var socketSnapshot=moneySnapshot with { Session="test_session",Epoch=3 };
var server=Task.Run(async () =>
{
    for(int attempt=0;attempt<2;attempt++)
    {
        using var peer=await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var reader=new StreamReader(peer.GetStream());
        using var writer=new StreamWriter(peer.GetStream()) { AutoFlush=true,NewLine="\n" };
        Check(await reader.ReadLineAsync()=="AUTH token","Client authentication");
        await writer.WriteLineAsync("HELLO crc test_session 3 3");
        string? line;
        while((line=await reader.ReadLineAsync()) is not null)
        {
            if(line.StartsWith("STATUS "))
            {
                statusQueries++;
                if(statusQueries==4) await writer.WriteLineAsync("STALE "+pendingEdit.Identity);
                else await writer.WriteLineAsync(receipt ?? "UNKNOWN "+pendingEdit.Identity);
            }
            if(line.StartsWith("COMMIT "))
            {
                commits++;
                money=1001;
                receipt=$"APPLIED {pendingEdit.Identity} 1 123";
                money--;
                break;
            }
        }
    }
});
try
{
    bool lost=false;
    using(var first=new EmulatorClient(port,"token","crc"))
        try { first.Apply(socketSnapshot,moneyPatches,pendingEdit); }
        catch(IOException) { lost=true; }
    Check(lost,"Injected lost acknowledgement");
    using(var second=new EmulatorClient(port,"token","crc"))
    {
        Check(second.Apply(socketSnapshot,moneyPatches,pendingEdit)==123,"Recovered original result");
        Check(second.Result(pendingEdit)==123,"Repeated result query");
        rejected=false;
        try { second.Result(pendingEdit); }
        catch(StaleTransactionException) { rejected=true; }
        Check(rejected,"Changed timeline rejected");
    }
    server.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    Check(commits==1 && money==1000,"Lost receipt cannot overwrite game spend");
}
finally { listener.Stop(); }
Console.WriteLine("PASS native complete-field, stale-version, lost-receipt and timeline regressions.");

SaveCommitTests.Run(File.ReadAllBytes(saveFixturePath));
LiveMergeTests.Run(saveFixturePath);
}
finally
{
    if (temporarySaveFixture is not null) File.Delete(temporarySaveFixture);
}
