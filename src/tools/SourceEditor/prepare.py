"""Stage source-level startup and save-service integration for PKHeX-Avalonia."""

from pathlib import Path
import shutil
import sys
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[2]


def replace(path, before, after):
    text = path.read_text()
    if text.count(before) != 1:
        raise RuntimeError(f"Upstream integration point changed in {path.name}")
    path.write_text(text.replace(before, after))


def prepare(target):
    shutil.copytree(ROOT / "upstream/PKHeX-Avalonia", target,
                    ignore=shutil.ignore_patterns("bin", "obj", ".git"))
    application = target / "PKHeX.Application/Abstractions"
    (application / "ISaveImageWriter.cs").write_text('''namespace PKHeX.Application.Abstractions;

/// <summary>Commits a serialized save through its active storage owner.</summary>
public interface ISaveImageWriter
{
    IPreparedSaveWrite Prepare(string path);
}

public interface IPreparedSaveWrite : IDisposable
{
    bool HandlesBackup => false;
    void Write(byte[] data);
}
''')
    service = target / "PKHeX.Infrastructure/SaveFileService.cs"
    replace(service, "private readonly AppSettings _settings;", "private readonly AppSettings _settings;\n    private readonly ISaveImageWriter? _imageWriter;")
    replace(service, "SaveFileService(ISaveBackupService backupService, AppSettings settings)", "SaveFileService(ISaveBackupService backupService, AppSettings settings, ISaveImageWriter? imageWriter = null)")
    replace(service, "_settings = settings;", "_settings = settings;\n        _imageWriter = imageWriter;")
    replace(service, "long sessionId;", "long sessionId;\n        IPreparedSaveWrite? prepared = null;")
    replace(service, "sessionId = _sessionId;", "sessionId = _sessionId;\n                prepared = _imageWriter?.Prepare(savePath);")
    replace(service, "var result = await Task.Run(() => SaveSnapshot(save, originalSave, savePath, path is not null, sessionId));", "using var writeScope = prepared;\n        var result = await Task.Run(() => SaveSnapshot(save, originalSave, savePath, path is not null, sessionId, prepared));")
    replace(service, "bool updatePath, long sessionId)", "bool updatePath, long sessionId, IPreparedSaveWrite? prepared)")
    replace(service, "File.WriteAllBytes(savePath, data);", "if (prepared is null) File.WriteAllBytes(savePath, data);\n                else prepared.Write(data);")
    replace(service, "if (_settings.Backup.BAKEnabled && File.Exists(savePath))", "if (_settings.Backup.BAKEnabled && prepared?.HandlesBackup != true && File.Exists(savePath))")
    host = target / "PKHeX.Avalonia"
    (host / "Services/BridgeSaveImageWriter.cs").write_text('''using PKHeX.Application.Abstractions;
using PKHeX.GbaBridge;

namespace PKHeX.Avalonia.Services;

public sealed class BridgeSaveImageWriter : ISaveImageWriter
{
    internal Func<string,IPreparedSaveWrite>? PrepareLive { get; set; }
    public IPreparedSaveWrite Prepare(string path) => PrepareLive?.Invoke(path) ?? new LocalWrite(path);

    internal sealed class LocalWrite(string path) : IPreparedSaveWrite
    {
        public void Write(byte[] data) => BridgeModule.WriteAllBytes(path,data);
        public void Dispose() { }
    }
}
''')
    shutil.copy2(Path(__file__).parent / "AutoLiveWorkspace.cs",host / "Services/AutoLiveWorkspace.cs")
    shutil.copy2(Path(__file__).parent / "LiveViewModels.cs",target / "PKHeX.Presentation/ViewModels/LiveViewModels.cs")
    shutil.copy2(Path(__file__).parent / "PreparedWriteTests.cs",target / "Tests/PKHeX.Avalonia.Tests/PreparedWriteTests.cs")
    trainer = target / "PKHeX.Presentation/ViewModels/TrainerEditorViewModel.cs"
    replace(trainer,"        LoadZaFields();\n    }", "        LoadZaFields();\n        RememberLiveDraft();\n    }")
    replace(trainer,"        if (battleBoxChanged) RaiseSaveSlotsChanged();", "        if (battleBoxChanged) RaiseSaveSlotsChanged();\n        RememberLiveDraft();")
    pokemon = target / "PKHeX.Presentation/ViewModels/PokemonEditorViewModel.cs"
    replace(pokemon,"        RerollPidCommand.NotifyCanExecuteChanged();\n    }", "        RerollPidCommand.NotifyCanExecuteChanged();\n        RememberLiveDraft();\n    }")
    slots = target / "PKHeX.Presentation/ViewModels/MainWindowViewModel.SlotOperations.cs"
    replace(slots,"        CurrentSave.SetBoxSlotAtIndex(pk, box, slot);", "        CurrentSave.SetBoxSlotAtIndex(pk, box, slot);\n        CurrentPokemonEditor.RememberLiveDraft();")
    replace(slots,"        CurrentSave.SetPartySlotAtIndex(pk, slot);", "        CurrentSave.SetPartySlotAtIndex(pk, slot);\n        CurrentPokemonEditor.RememberLiveDraft();")
    app = host / "App.axaml.cs"
    replace(app, "settings.IsHaXMode = startup.HaX;", "settings.IsHaXMode = true;")
    replace(app, "base.OnFrameworkInitializationCompleted();", "base.OnFrameworkInitializationCompleted();\n        PKHeX.GbaBridge.BridgeModule.Attach();\n        PKHeX.GbaBridge.BridgeModule.ManageLiveWorkspace();\n        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime liveDesktop && liveDesktop.MainWindow is { } liveWindow)\n            Services.GetRequiredService<AutoLiveWorkspace>().Start(liveWindow, liveDesktop);")
    replace(app, "services.AddInfrastructure();", "services.AddInfrastructure();\n        services.AddSingleton<BridgeSaveImageWriter>();\n        services.AddSingleton<ISaveImageWriter>(sp => sp.GetRequiredService<BridgeSaveImageWriter>());\n        services.AddSingleton<AutoLiveWorkspace>();")
    project = host / "PKHeX.Avalonia.csproj"
    references = "\n  <ItemGroup>\n"
    for path in (ROOT / "PKHeX.GbaBridge.csproj", ROOT / "tools/ChineseCompat/ChineseCompat.csproj"):
        references += f'    <ProjectReference Include="{escape(str(path))}" />\n'
    references += '''  </ItemGroup>
  <Target Name="ApplyGbaChineseCodec" AfterTargets="PrepareForBundle" Condition="'$(GbaPatcher)' != ''">
    <ItemGroup>
      <GbaBundleCore Include="@(FilesToBundle)" Condition="'%(Filename)%(Extension)' == 'PKHeX.Core.dll'" />
    </ItemGroup>
    <Error Condition="'@(GbaBundleCore)' == ''" Text="Core assembly missing from single-file bundle inputs" />
    <Exec Command="&quot;$(GbaDotnet)&quot; &quot;$(GbaPatcher)&quot; &quot;@(GbaBundleCore->'%(FullPath)')&quot; &quot;$(TargetDir)PKHeX.ChineseCompat.dll&quot; &quot;$(TargetDir)PKHeX.Core.chinese.dll&quot;" />
    <ItemGroup>
      <FilesToBundle Remove="@(GbaBundleCore)" />
      <FilesToBundle Include="$(TargetDir)PKHeX.Core.chinese.dll" RelativePath="PKHeX.Core.dll" />
    </ItemGroup>
  </Target>
</Project>'''
    replace(project, "</Project>", references)
    return target


if __name__ == "__main__":
    print(prepare(Path(sys.argv[1]).resolve()))
