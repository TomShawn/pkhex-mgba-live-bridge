using Mono.Cecil;
using Mono.Cecil.Cil;

if (args[0] == "bundle")
{
    var bytes = File.ReadAllBytes(args[1]);
    var signature = Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE");
    int marker = bytes.AsSpan().IndexOf(signature);
    if (marker < 8) throw new Exception("Native bundle marker missing");
    using var stream = new FileStream(args[1], FileMode.Open, FileAccess.ReadWrite);
    var imageType = typeof(Microsoft.NET.HostModel.AppHost.HostWriter).Assembly.GetType("Microsoft.NET.HostModel.MachO.MachObjectFile")!;
    var image = imageType.GetMethod("Create", [typeof(Stream)])!.Invoke(null, [stream])!;
    var access = new Microsoft.NET.HostModel.MachO.StreamBasedMachOFile(stream);
    imageType.GetMethod("TryAdjustHeadersForBundle")!.Invoke(image, [(ulong)stream.Length, access]);
    stream.Position = marker - 8;
    using var writer = new BinaryWriter(stream);
    writer.Write(long.Parse(args[2]));
    Console.WriteLine("Updated native bundle header and Mach-O segment sizes.");
    return;
}

if (args[0] == "saveguard")
{
    using var application = AssemblyDefinition.ReadAssembly(args[1]);
    using var bridge = AssemblyDefinition.ReadAssembly(args[2]);
    var guard = bridge.MainModule.GetType("PKHeX.GbaBridge.BridgeModule").Methods.Single(method => method.Name == "WriteAllBytes");
    var method = application.MainModule.GetType("PKHeX.Infrastructure.SaveFileService").Methods.Single(method => method.Name == "SaveSnapshot");
    var writes = method.Body.Instructions.Where(instruction => instruction.Operand is MethodReference target
        && target.DeclaringType.FullName == "System.IO.File" && target.Name == "WriteAllBytes").ToArray();
    if (writes.Length != 1) throw new Exception("Central save write entry changed");
    writes[0].Operand = application.MainModule.ImportReference(guard);
    application.Write(args[3]);
    Console.WriteLine("Installed stale-editor guard at the central disk write entry.");
    return;
}

if (args[0] == "bridge")
{
    var bridgeResolver = new DefaultAssemblyResolver();
    bridgeResolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
    bridgeResolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
    bridgeResolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
    using var application = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { AssemblyResolver = bridgeResolver });
    using var bridge = AssemblyDefinition.ReadAssembly(args[2], new ReaderParameters { AssemblyResolver = bridgeResolver });
    var entry = application.MainModule.GetType("PKHeX.Avalonia.App").Methods.Single(method => method.Name == "OnFrameworkInitializationCompleted");
    var attach = bridge.MainModule.GetType("PKHeX.GbaBridge.BridgeModule").Methods.Single(method => method.Name == "Attach");
    var reference = application.MainModule.ImportReference(attach);
    var processor = entry.Body.GetILProcessor();
    foreach (var instruction in entry.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ret).ToArray())
    {
        instruction.OpCode = OpCodes.Call;
        instruction.Operand = reference;
        processor.InsertAfter(instruction,Instruction.Create(OpCodes.Ret));
    }
    application.Write(args[3]);
    Console.WriteLine("Integrated bridge initialization into the application lifecycle.");
    return;
}

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
using var core = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
using var helper = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { AssemblyResolver = resolver });
var converter = core.MainModule.GetType("PKHeX.Core.StringConverter3");
var codec = helper.MainModule.GetType("PKHeX.ChineseCompat.ChineseCodec");
foreach (var name in new[] { "LoadString", "SetString" })
{
    int languageIndex = name == "LoadString" ? 2 : 3;
    int parameterCount = name == "LoadString" ? 3 : 5;
    var method = converter.Methods.Single(method => method.Name == name
        && method.Parameters.Count == parameterCount
        && method.Parameters[languageIndex].ParameterType.MetadataType == MetadataType.Int32);
    var implementation = codec.Methods.Single(method => method.Name == name);
    method.Body = new MethodBody(method);
    var il = method.Body.GetILProcessor();
    foreach (var parameter in method.Parameters)
        il.Emit(OpCodes.Ldarg, parameter);
    il.Emit(OpCodes.Call, core.MainModule.ImportReference(implementation));
    il.Emit(OpCodes.Ret);
    Console.WriteLine($"Patched {method.FullName}");
}
core.Write(args[2]);
