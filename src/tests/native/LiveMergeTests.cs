using PKHeX.GbaBridge;

internal static class LiveMergeTests
{
    internal static void Run(string path)
    {
        byte[] template = File.ReadAllBytes(path);
        SaveData original = SaveCodec.Parse(template);
        SaveData plain = SaveCodec.Canonical(original);
        var writing = new Snapshot(0,0,0,0,original,[],"test",0,template[..0x20000],original.Counter + 1);
        bool waiting = false;
        try { LiveGameConnection.Template(writing,template); }
        catch (BridgeException) { waiting = true; }
        CheckBusy(waiting);
        SaveData Copy() => new((byte[])plain.Small.Clone(),(byte[])plain.Large.Clone(),(byte[])plain.Boxes.Clone());
        void Check(bool value,string description)
        {
            if (!value) throw new Exception(description);
        }
        void CheckBusy(bool value) => Check(value,"advanced RAM save counter waits for Flash completion");

        SaveData baseline = Copy(), draft = Copy(), live = Copy();
        SaveCodec.Put32(baseline.Large,0x490,1000);
        SaveCodec.Put32(draft.Large,0x490,1001);
        SaveCodec.Put32(live.Large,0x490,744);
        SaveCodec.Put16(live.Large,0,9);
        var result = SaveCodec.Rebase(baseline,draft,live);
        Check(result.Conflicts.Count == 1 && result.Conflicts[0].Field == "金钱","same-field conflict exposes the money field");
        Check(SaveCodec.U32(result.Draft.Large,0x490) == 1001 && SaveCodec.U16(result.Draft.Large,0) == 9,"draft and new map progress coexist");
        Check(SaveCodec.U32(result.Baseline.Large,0x490) == 1000 && SaveCodec.U16(result.Baseline.Large,0) == 9,"dirty field retains its original baseline");
        result = SaveCodec.Rebase(result.Baseline,result.Draft,live);
        Check(result.Conflicts.Count == 1,"repeated refresh retains the conflict");
        result = SaveCodec.Rebase(baseline,draft,live,true);
        Check(SaveCodec.U32(result.Baseline.Large,0x490) == 744 && SaveCodec.U32(result.Draft.Large,0x490) == 1001,"explicit editor choice rebases on the observed game value");
        result = SaveCodec.Rebase(baseline,draft,live,false);
        Check(SaveCodec.U32(result.Draft.Large,0x490) == 744,"explicit game choice preserves the game value");

        live = Copy();
        SaveCodec.Put32(live.Large,0x490,1001);
        result = SaveCodec.Rebase(baseline,draft,live);
        Check(result.Conflicts.Count == 0 && result.Baseline.Large.SequenceEqual(result.Draft.Large),"already-applied edit becomes clean");

        baseline = Copy(); draft = Copy(); live = Copy();
        SaveCodec.Put16(draft.Large,0x652,73);
        SaveCodec.Put16(live.Large,0,9);
        live.Large[0x1280] ^= 2;
        result = SaveCodec.Rebase(baseline,draft,live);
        byte[] image = SaveCodec.Image(template,result.Draft);
        SaveData persisted = SaveCodec.Canonical(SaveCodec.Parse(image,true));
        Check(SaveCodec.U16(persisted.Large,0x652) == 73 && SaveCodec.U16(persisted.Large,0) == 9
            && persisted.Large[0x1280] == live.Large[0x1280],"complete live image keeps position and event progress");
        Check(image.Length == template.Length && image.AsSpan(0x20000).SequenceEqual(template.AsSpan(0x20000)),"RTC extension is preserved");

        var mon = SaveCodec.DecodeMon(baseline.Large[0x238..0x29C]);
        SaveCodec.Put16(mon,44,94);
        SaveCodec.EncodeMon(mon).CopyTo(draft.Large,0x238);
        byte[] first = live.Large[0x238..0x29C], second = live.Large[0x29C..0x300];
        second.CopyTo(live.Large,0x238); first.CopyTo(live.Large,0x29C);
        result = SaveCodec.Rebase(baseline,draft,live);
        Check(result.Conflicts.Count == 0 && SaveCodec.U16(SaveCodec.DecodeMon(result.Draft.Large[0x29C..0x300]),44) == 94,
            "edited party member follows its unique identity after a game reorder");

        baseline = Copy(); draft = Copy(); live = Copy();
        byte[] editorFirst = SaveCodec.DecodeMon(draft.Large[0x238..0x29C]);
        SaveCodec.Put16(editorFirst,44,94);
        byte[] encodedEditorFirst = SaveCodec.EncodeMon(editorFirst);
        byte[] editorSecond = draft.Large[0x29C..0x300];
        editorSecond.CopyTo(draft.Large,0x238);
        encodedEditorFirst.CopyTo(draft.Large,0x29C);
        byte[] liveFirst = SaveCodec.DecodeMon(live.Large[0x238..0x29C]);
        SaveCodec.Put16(liveFirst,86,321);
        SaveCodec.EncodeMon(liveFirst).CopyTo(live.Large,0x238);
        result = SaveCodec.Rebase(baseline,draft,live);
        var movedInEditor = SaveCodec.DecodeMon(result.Draft.Large[0x29C..0x300]);
        Check(result.Conflicts.Count == 0
            && result.Draft.Large.AsSpan(0x238,8).SequenceEqual(baseline.Large.AsSpan(0x29C,8))
            && result.Draft.Large.AsSpan(0x29C,8).SequenceEqual(baseline.Large.AsSpan(0x238,8))
            && SaveCodec.U16(movedInEditor,44) == 94
            && SaveCodec.U16(movedInEditor,86) == 321,
            "editor party swap preserves live HP and edited move without false whole-slot conflict");
        var repeatedSwap = SaveCodec.Rebase(result.Baseline,result.Draft,live);
        var repeatedMoved = SaveCodec.DecodeMon(repeatedSwap.Draft.Large[0x29C..0x300]);
        Check(repeatedSwap.Conflicts.Count == 0
            && repeatedSwap.Draft.Large.AsSpan(0x238,8).SequenceEqual(baseline.Large.AsSpan(0x29C,8))
            && SaveCodec.U16(repeatedMoved,44) == 94
            && SaveCodec.U16(repeatedMoved,86) == 321,
            "repeated live refresh after editor party swap remains conflict-free and keeps live HP");

        baseline = Copy(); draft = Copy(); live = Copy();
        byte[] boxMon = SaveCodec.DecodeMon(baseline.Large[0x238..0x288]);
        SaveCodec.EncodeMon(boxMon).CopyTo(baseline.Boxes,4);
        SaveCodec.EncodeMon(boxMon).CopyTo(live.Boxes,4);
        Array.Clear(draft.Boxes,4,80);
        SaveCodec.Put16(boxMon,44,94);
        SaveCodec.EncodeMon(boxMon).CopyTo(draft.Boxes,84);
        result = SaveCodec.Rebase(baseline,draft,live);
        Check(result.Conflicts.Count == 0
            && (result.Draft.Boxes[4 + 19] & 2) == 0
            && SaveCodec.U16(SaveCodec.DecodeMon(result.Draft.Boxes[84..164]),44) == 94,
            "moving a box Pokemon to an empty slot vacates the source and merges edits");

        string temporary = Path.Combine(Path.GetTempPath(),"gba-live-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var coordinator = new SaveCommitCoordinator(temporary);
            string leasedPath = Path.Combine(temporary,"test.sav");
            using (coordinator.ClaimLiveSave(leasedPath,CancellationToken.None))
            {
                using var cancelled = new CancellationTokenSource(150);
                bool stopped = false;
                try { using var other = coordinator.ClaimLiveSave(leasedPath,cancelled.Token); }
                catch (OperationCanceledException) { stopped = true; }
                Check(stopped,"concurrent save lease waits without losing cancellation");
            }
            using var released = coordinator.ClaimLiveSave(leasedPath,CancellationToken.None);
        }
        finally { Directory.Delete(temporary,true); }

        SaveData encodedBase = SaveCodec.EncodeWithKey(baseline,0xDEADBEEF);
        SaveData encodedDraft = SaveCodec.EncodeWithKey(draft,1234);
        SaveData encodedLive = SaveCodec.EncodeWithKey(live,0x89ABCDEF);
        result = SaveCodec.Rebase(encodedBase,encodedDraft,encodedLive);
        Check(SaveCodec.U32(result.Draft.Small,0xAC) == 0x89ABCDEF,"rebased draft uses the current encryption key");
        Console.WriteLine("PASS live refresh, conflict resolution, party identity, complete image and RTC regressions.");
    }
}
