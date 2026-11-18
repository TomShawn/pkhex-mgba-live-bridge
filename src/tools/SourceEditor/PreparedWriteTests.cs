using Moq;
using PKHeX.Application.Abstractions;
using PKHeX.Application.Services;
using PKHeX.Core;
using PKHeX.Infrastructure;
using PKHeX.GbaBridge;
using PKHeX.Presentation.ViewModels;
using PKHeX.Avalonia.Tests.Harness;
using Avalonia.Headless.XUnit;
using Xunit;

namespace PKHeX.Avalonia.Tests;

public sealed class PreparedWriteTests
{
    [AvaloniaFact]
    public void LiveRefreshNotifiesTheTrainerNameHeader()
    {
        string fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../savefiles/gen3_emerald.sav"));
        var save = new SAV3E(File.ReadAllBytes(fixture));
        using var app = new HeadlessAppFixture();
        app.LoadSaveInstance(save,"test.sav");
        var updated = new SAV3E(save.Write()) { OT = "NAME" };
        Assert.PropertyChanged(app.ViewModel,nameof(app.ViewModel.CurrentTrainerName),
            () => app.ViewModel.RefreshLiveImage(updated.Write().ToArray(),false));
        Assert.Equal("NAME",app.ViewModel.CurrentTrainerName);
    }

    [AvaloniaFact]
    public void LiveRefreshAcrossSaveBankSwitchKeepsFutureEditsSerializable()
    {
        string fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../savefiles/gen3_emerald.sav"));
        var original = new SAV3E(File.ReadAllBytes(fixture)) { Money = 5555 };
        byte[] image = original.Write().ToArray();
        byte[] first = image[..0xE000];
        image.AsSpan(0xE000,0xE000).CopyTo(image);
        first.CopyTo(image,0xE000);
        var game = new SAV3E(image) { Money = 4444 };
        using var app = new HeadlessAppFixture();
        app.LoadSaveInstance(original,"test.sav");
        app.ViewModel.SelectedWorkspaceIndex = 2;
        ((IBoxNavigator)app.ViewModel.BoxViewer!).NavigateTo(3,7);
        app.ViewModel.RefreshLiveImage(game.Write().ToArray(),false);
        Assert.Equal(2,app.ViewModel.SelectedWorkspaceIndex);
        Assert.Equal(3,app.ViewModel.BoxViewer!.CurrentBox);
        Assert.Equal(7,app.ViewModel.BoxViewer!.SelectedIndex);
        Assert.Equal(game.CurrentBox,app.Save!.CurrentBox);
        Assert.Same(app.Gateway.CurrentSave,app.ViewModel.CurrentSave);
        var trainer = app.ViewModel.TrainerEditor!;
        Assert.Equal(4444u,trainer.Money);
        trainer.Money = 7777;
        trainer.SaveCommand.Execute(null);
        Assert.Equal(7777u,app.Save!.Money);
        Assert.Equal(7777u,new SAV3E(app.Save.Write()).Money);
    }

    [AvaloniaFact]
    public void EventFlagsTrackUnappliedDraftsAndRefreshFromLiveSnapshots()
    {
        string fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../savefiles/gen3_emerald.sav"));
        var save = new SAV3E(File.ReadAllBytes(fixture));
        using var app = new HeadlessAppFixture();
        app.LoadSaveInstance(save,"test.sav");
        var events = Assert.IsType<EventFlagsEditorViewModel>(app.ViewModel.EventFlagsEditor);
        bool initial = events.Flags[10].IsSet;
        events.Flags[10].IsSet = !initial;
        Assert.True(events.HasLiveDraft);
        Assert.True(app.ViewModel.HasLiveDraft);
        events.SaveCommand.Execute(null);
        Assert.False(events.HasLiveDraft);
        Assert.False(app.ViewModel.HasLiveDraft);

        var game = new SAV3E(app.Save!.Write());
        game.SetEventFlag(25,true);
        app.ViewModel.RefreshLiveImage(game.Write().ToArray(),false);
        Assert.True(events.Flags[25].IsSet);
    }

    [Fact]
    public void AppliedTrainerDraftSurvivesRepeatedLiveRefresh()
    {
        string fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../savefiles/gen3_emerald.sav"));
        var save = new SAV3E(File.ReadAllBytes(fixture));
        save.Money = 5555;
        byte[] baseline = save.Write().ToArray();
        var trainer = new TrainerEditorViewModel(save);
        trainer.Money = 7777;
        Assert.True(trainer.HasLiveDraft);
        Assert.True(trainer.SaveCommand.CanExecute(null));
        trainer.SaveCommand.Execute(null);
        Assert.Equal(7777u,save.Money);
        Assert.False(trainer.HasLiveDraft);
        var game = new SAV3E((byte[])baseline.Clone()) { Money = 4444 };
        var snapshot = new LiveSnapshot(game.Write().ToArray(),"test",1);
        for (int refresh = 0; refresh < 3; refresh++)
        {
            var merged = LiveGameConnection.Rebase(baseline,save.Write().ToArray(),snapshot);
            Assert.Contains(merged.Conflicts,item => item.Field == "金钱");
            save.CopyChangesFrom(new SAV3E(merged.Draft));
            baseline = merged.Baseline;
            Assert.Equal(7777u,save.Money);
        }
    }

    [Fact]
    public async Task SaveCapturesWriterContextBeforeWorkerSerializationAndAlwaysDisposesIt()
    {
        string path = Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N") + ".sav");
        var writer = new Writer();
        var service = new SaveFileService(Mock.Of<ISaveBackupService>(),new AppSettings(),writer);
        var save = new SAV3E(new byte[0x20000]);
        service.OpenLoadedSave(save,path);
        writer.Revision = 7;
        Task<bool> pending = service.SaveFileAsync();
        writer.Revision = 8;
        Assert.True(await pending);
        Assert.Equal(7,writer.CapturedRevision);
        Assert.True(writer.Written);
        Assert.True(writer.Disposed);
        writer.Fail = true;
        writer.Disposed = false;
        Assert.False(await service.SaveFileAsync());
        Assert.True(writer.Disposed);
    }

    private sealed class Writer : ISaveImageWriter
    {
        internal int Revision;
        internal int CapturedRevision;
        internal bool Written;
        internal bool Disposed;
        internal bool Fail;
        public IPreparedSaveWrite Prepare(string path) => new Prepared(this,Revision);
        private sealed class Prepared(Writer writer, int revision) : IPreparedSaveWrite
        {
            public void Write(byte[] data)
            {
                writer.CapturedRevision = revision;
                if (writer.Fail) throw new IOException("Injected write failure");
                writer.Written = true;
            }
            public void Dispose() => writer.Disposed = true;
        }
    }
}
