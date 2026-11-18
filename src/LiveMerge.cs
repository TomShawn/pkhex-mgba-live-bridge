namespace PKHeX.GbaBridge;

public sealed record LiveFieldConflict(string Field, string Baseline, string Current, string Edited);
internal sealed record LiveMergeResult(SaveData Baseline, SaveData Draft, List<LiveFieldConflict> Conflicts);

internal static partial class SaveCodec
{
    internal static SaveData Effective(Snapshot snapshot)
    {
        byte[] large = (byte[])snapshot.Raw.Large.Clone();
        Put32(large,0x234,snapshot.Party[0]);
        snapshot.Party.AsSpan(1).CopyTo(large.AsSpan(0x238));
        return snapshot.Raw with { Large = large };
    }

    internal static byte[] Image(byte[] template, SaveData state)
    {
        SaveData disk = Parse(template,true);
        byte[] image = (byte[])template.Clone();
        int[] starts = [0,14 * 4096];
        var banks = starts.Where(start => Enumerable.Range(0,14).All(index =>
        {
            int offset = start + index * 4096;
            int section = U16(image,offset + 0xFF4);
            return section < 14 && U32(image,offset + 0xFFC) == disk.Counter
                && U32(image,offset + 0xFF8) == 0x08012025
                && Checksum(image[offset..(offset + Sizes[section])]) == U16(image,offset + 0xFF6);
        }) && Enumerable.Range(0,14).Select(index => U16(image,start + index * 4096 + 0xFF4)).Distinct().Count() == 14).ToArray();
        foreach (int bank in banks)
        for (int index = 0; index < 14; index++)
        {
            int offset = bank + index * 4096;
            int section = U16(image,offset + 0xFF4);
            ReadOnlySpan<byte> payload = section == 0 ? state.Small
                : section < 5 ? state.Large.AsSpan((section - 1) * 0xF80,Sizes[section])
                : state.Boxes.AsSpan((section - 5) * 0xF80,Sizes[section]);
            payload.CopyTo(image.AsSpan(offset));
            Put16(image,offset + 0xFF6,Checksum(payload.ToArray()));
        }
        Parse(image,true);
        return image;
    }

    internal static LiveMergeResult Rebase(SaveData baseline, SaveData draft, SaveData current, bool? preferDraft = null)
    {
        SaveData old = Canonical(baseline), edited = Canonical(draft), live = Canonical(current);
        if (!old.Small.AsSpan(10,4).SequenceEqual(live.Small.AsSpan(10,4))
            && !edited.Small.AsSpan(10,4).SequenceEqual(live.Small.AsSpan(10,4)))
            throw new LiveTimelineException("游戏训练家身份已经变化，旧编辑草稿需要基于当前游戏重新处理。");
        var conflicts = new List<LiveFieldConflict>();
        byte[] smallBase = (byte[])live.Small.Clone(), smallDraft = (byte[])live.Small.Clone();
        byte[] largeBase = (byte[])live.Large.Clone(), largeDraft = (byte[])live.Large.Clone();
        byte[] boxBase = (byte[])live.Boxes.Clone(), boxDraft = (byte[])live.Boxes.Clone();

        void Field(byte[] before, byte[] after, byte[] active, byte[] rebased, byte[] merged, int offset, int size, string name)
        {
            var previous = before.AsSpan(offset,size);
            var desired = after.AsSpan(offset,size);
            var latest = active.AsSpan(offset,size);
            if (previous.SequenceEqual(desired) || latest.SequenceEqual(desired)) return;
            bool conflict = !previous.SequenceEqual(latest);
            if (conflict)
                conflicts.Add(new LiveFieldConflict(name,Display(previous),Display(latest),Display(desired)));
            if (conflict && preferDraft == false) return;
            desired.CopyTo(merged.AsSpan(offset));
            if (!conflict || preferDraft is null) previous.CopyTo(rebased.AsSpan(offset));
        }

        void Region(byte[] before, byte[] after, byte[] active, byte[] rebased, byte[] merged,
            IEnumerable<(int Offset, int Size)> fields, string name, params (int Start,int End)[] bits)
        {
            var covered = new bool[before.Length];
            foreach (var (offset,size) in fields)
            {
                Array.Fill(covered,true,offset,size);
                Field(before,after,active,rebased,merged,offset,size,FieldName(name,offset));
            }
            int index = 0;
            while (index < before.Length)
            {
                if (covered[index]) { index++; continue; }
                if (bits.Any(range => index >= range.Start && index < range.End))
                {
                    int mask = before[index] ^ after[index];
                    merged[index] = (byte)((active[index] & ~mask) | (after[index] & mask));
                    rebased[index] = (byte)((active[index] & ~mask) | (before[index] & mask));
                    index++;
                    continue;
                }
                int end = index + 1;
                while (end < before.Length && end - index < 256 && !covered[end]
                    && !bits.Any(range => end >= range.Start && end < range.End)) end++;
                Field(before,after,active,rebased,merged,index,end - index,$"{name} 0x{index:X}");
                index = end;
            }
        }

        void Slots(byte[] before, byte[] after, byte[] active, byte[] rebased, byte[] merged, int start, int count, int width, string name)
        {
            int[] FindMatches(byte[] region, ReadOnlySpan<byte> identity)
            {
                var identityBytes = identity[..8].ToArray();
                return Enumerable.Range(0,count).Where(index =>
                    Occupied(region[(start + index * width)..(start + (index + 1) * width)])
                    && region.AsSpan(start + index * width,8).SequenceEqual(identityBytes)).ToArray();
            }

            var targets = new HashSet<int>();
            for (int slot = 0; slot < count; slot++)
            {
                int source = start + slot * width;
                byte[] previous = before[source..(source + width)], desired = after[source..(source + width)];
                if (previous.SequenceEqual(desired)) continue;
                int destination = slot;
                int baselineSlot = slot;
                bool ambiguous = false;
                if (Occupied(previous) && SameIdentity(previous,desired))
                {
                    var matches = FindMatches(active,previous);
                    if (matches.Length == 1) destination = baselineSlot = matches[0];
                    else if (matches.Length > 1)
                    {
                        var exact = matches.Where(index => active.AsSpan(start + index * width,width).SequenceEqual(previous)).ToArray();
                        if (exact.Length == 1) destination = baselineSlot = exact[0];
                        else ambiguous = !active.AsSpan(source,width).SequenceEqual(previous);
                    }
                }
                else if (Occupied(desired))
                {
                    byte[] currentAtSlot = active[source..(source + width)];
                    bool slotOccupantUnchanged = (Occupied(previous) && Occupied(currentAtSlot) && SameIdentity(previous,currentAtSlot))
                        || (!Occupied(previous) && !Occupied(currentAtSlot))
                        || (Occupied(currentAtSlot) && SameIdentity(desired,currentAtSlot));
                    var beforeMatches = FindMatches(before,desired);
                    var afterMatches = FindMatches(after,desired);
                    var activeMatches = FindMatches(active,desired);
                    if (slotOccupantUnchanged && beforeMatches.Length == 1 && afterMatches.Length == 1 && activeMatches.Length == 1)
                    {
                        int oldSource = start + beforeMatches[0] * width;
                        previous = before[oldSource..(oldSource + width)];
                        baselineSlot = activeMatches[0];
                    }
                }
                int offset = start + destination * width;
                int baselineOffset = start + baselineSlot * width;
                byte[] latest = active[baselineOffset..(baselineOffset + width)];
                string label = $"{name} {slot + 1}";
                if (!targets.Add(destination))
                    throw new BridgeException(label + "存在多个编辑目标，请处理宝可梦位置后重试。");
                if (!Occupied(desired) && Occupied(previous) && Occupied(latest) && SameIdentity(previous,latest)
                    && FindMatches(before,previous).Length == 1 && FindMatches(after,previous).Length == 1 && FindMatches(active,previous).Length == 1)
                {
                    desired.CopyTo(merged,offset);
                    continue;
                }
                if (ambiguous || !Occupied(previous) || !Occupied(desired) || !SameIdentity(previous,desired) || !SameIdentity(previous,latest))
                {
                    byte[] nextBase = (byte[])latest.Clone(), nextDraft = (byte[])latest.Clone();
                    Field(previous,desired,latest,nextBase,nextDraft,0,width,label + " 个体或槽位");
                    nextBase.CopyTo(rebased,baselineOffset);
                    nextDraft.CopyTo(merged,offset);
                    continue;
                }
                byte[] p = DecodeMon(previous), d = DecodeMon(desired), l = DecodeMon(latest);
                byte[] b = (byte[])l.Clone(), m = (byte[])l.Clone();
                IEnumerable<(int Offset,int Size)> fields = MonFields;
                if (width == 100)
                    fields = fields.Concat(new[] { (80,4),(84,1),(85,1) }).Concat(Enumerable.Range(0,7).Select(index => (86 + index * 2,2)));
                foreach (var (field,size) in fields)
                    Field(p,d,l,b,m,field,size,$"{label} 字段 0x{field:X}");
                EncodeMon(b).CopyTo(rebased,baselineOffset);
                EncodeMon(m).CopyTo(merged,offset);
            }
        }

        Region(old.Small,edited.Small,live.Small,smallBase,smallDraft,SmallFields,"人物",(0x28,0x90));
        var largeFields = LargeFields.Where(field => field != (0x238,600)).Concat(new[] { (0x238,600) });
        // Party records are merged separately and must remain outside the generic region.
        var largeOld = (byte[])old.Large.Clone();
        var largeEdited = (byte[])edited.Large.Clone();
        live.Large.AsSpan(0x238,600).CopyTo(largeOld.AsSpan(0x238));
        live.Large.AsSpan(0x238,600).CopyTo(largeEdited.AsSpan(0x238));
        Region(largeOld,largeEdited,live.Large,largeBase,largeDraft,largeFields,"游戏",(0x988,0x9BC),(0x1270,0x139C),(0x3B24,0x3B58));
        Slots(old.Large,edited.Large,live.Large,largeBase,largeDraft,0x238,6,100,"队伍");
        Region(old.Boxes[..4],edited.Boxes[..4],live.Boxes[..4],boxBase,boxDraft,[(0,4)],"箱子");
        Slots(old.Boxes,edited.Boxes,live.Boxes,boxBase,boxDraft,4,420,80,"箱子宝可梦");
        const int tail = 4 + 420 * 80;
        for (int box = 0; box < 14; box++)
        {
            Field(old.Boxes,edited.Boxes,live.Boxes,boxBase,boxDraft,tail + box * 9,9,$"箱子 {box + 1} 名称");
            Field(old.Boxes,edited.Boxes,live.Boxes,boxBase,boxDraft,tail + 126 + box,1,$"箱子 {box + 1} 壁纸");
        }
        uint key = U32(current.Small,0xAC);
        return new LiveMergeResult(EncodeWithKey(new SaveData(smallBase,largeBase,boxBase),key),
            EncodeWithKey(new SaveData(smallDraft,largeDraft,boxDraft),key),conflicts);
    }

    private static string Display(ReadOnlySpan<byte> value) => value.Length switch
    {
        1 => value[0].ToString(),
        2 => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(value).ToString(),
        4 => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(value).ToString(),
        _ => Convert.ToHexString(value[..Math.Min(value.Length,20)])
    };

    private static string FieldName(string region, int offset) => (region,offset) switch
    {
        ("人物",0) => "训练家名字", ("人物",10) => "训练家 ID", ("游戏",0x490) => "金钱",
        ("游戏",0x494) => "代币", ("游戏",0x234) => "队伍数量",
        ("游戏",>= 0x498 and < 0x848) => $"道具槽 {(offset - 0x498) / 4 + 1}",
        _ => $"{region} 0x{offset:X}"
    };
}
