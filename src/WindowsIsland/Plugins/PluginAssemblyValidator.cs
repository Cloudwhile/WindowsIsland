using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

// 只读 PE 元数据，不加载程序集、不运行构造函数或模块初始化器。
internal sealed class PluginAssemblyValidator : IDisposable
{
    private sealed record Image(FileStream Stream, PEReader Pe, MetadataReader Reader);
    private readonly Dictionary<string, Image> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _platform = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
        .Split(Path.PathSeparator).Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
    private string _root = "";

    public static void Validate(string root, PluginManifest manifest)
    {
        using var validator = new PluginAssemblyValidator { _root = root };
        var image = validator.Open(PluginPaths.Relative(root, manifest.EntryAssembly));
        var reader = image.Reader;
        var handle = reader.TypeDefinitions.FirstOrDefault(h => validator.FullName(reader, h) == manifest.EntryType);
        if (handle.IsNil) throw new InvalidDataException("找不到清单声明的入口类型。");
        var type = reader.GetTypeDefinition(handle);
        if ((type.Attributes & TypeAttributes.Abstract) != 0 || (type.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public
            || !validator.Implements(image, handle, new HashSet<string>())) throw new InvalidDataException("插件入口必须是公开的插件实现类型。");
        var constructor = type.GetMethods().Select(reader.GetMethodDefinition).Any(method =>
        {
            if (reader.GetString(method.Name) != ".ctor" || (method.Attributes & MethodAttributes.Public) == 0) return false;
            var signature = reader.GetBlobReader(method.Signature);
            var header = signature.ReadSignatureHeader();
            if (header.IsGeneric) signature.ReadCompressedInteger();
            return signature.ReadCompressedInteger() == 0;
        });
        if (!constructor) throw new InvalidDataException("插件入口缺少公开的无参数构造函数。");
        foreach (var dependency in validator._images.Values.ToArray()) validator.CheckReferences(dependency, new HashSet<string>());
    }

    private string FullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        return reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
    }

    private bool Implements(Image image, EntityHandle handle, HashSet<string> visited)
    {
        if (handle.IsNil) return false;
        if (handle.Kind == HandleKind.TypeReference)
        {
            var reference = image.Reader.GetTypeReference((TypeReferenceHandle)handle);
            var name = image.Reader.GetString(reference.Namespace) + "." + image.Reader.GetString(reference.Name);
            if (reference.ResolutionScope.Kind != HandleKind.AssemblyReference) return false;
            var assembly = image.Reader.GetString(image.Reader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name);
            if (assembly == typeof(IIslandPlugin).Assembly.GetName().Name) return name is "WindowsIsland.Contracts.IIslandPlugin" or "WindowsIsland.Contracts.IMessageSource";
            var path = PluginPaths.Relative(_root, assembly + ".dll");
            if (!File.Exists(path)) return false;
            var dependency = Open(path);
            var found = dependency.Reader.TypeDefinitions.FirstOrDefault(h => FullName(dependency.Reader, h) == name);
            return !found.IsNil && Implements(dependency, found, visited);
        }
        if (handle.Kind != HandleKind.TypeDefinition) return false;
        var identity = image.Stream.Name + "/" + FullName(image.Reader, (TypeDefinitionHandle)handle);
        if (!visited.Add(identity)) return false;
        var type = image.Reader.GetTypeDefinition((TypeDefinitionHandle)handle);
        return type.GetInterfaceImplementations().Any(i => Implements(image, image.Reader.GetInterfaceImplementation(i).Interface, visited))
            || Implements(image, type.BaseType, visited);
    }

    private Image Open(string path)
    {
        if (_images.TryGetValue(path, out var existing)) return existing;
        var stream = File.OpenRead(path);
        PEReader? pe = null;
        try
        {
            pe = new PEReader(stream);
            if (!pe.HasMetadata) throw new InvalidDataException("入口或依赖不是托管程序集。");
            var image = new Image(stream, pe, pe.GetMetadataReader());
            _images.Add(path, image);
            return image;
        }
        catch { pe?.Dispose(); stream.Dispose(); throw; }
    }

    private void CheckReferences(Image image, HashSet<string> visited)
    {
        if (!visited.Add(image.Stream.Name)) return;
        foreach (var handle in image.Reader.AssemblyReferences)
        {
            var reference = image.Reader.GetAssemblyReference(handle);
            var name = image.Reader.GetString(reference.Name);
            if (name == typeof(IIslandPlugin).Assembly.GetName().Name) continue;
            if (name == "WindowsIsland") throw new InvalidDataException("外部插件不能依赖主程序程序集。");
            var path = PluginPaths.Relative(_root, name + ".dll");
            if (File.Exists(path)) CheckReferences(Open(path), visited);
            else if (!_platform.Contains(name)) throw new InvalidDataException("插件包缺少依赖程序集。");
        }
    }

    public void Dispose() { foreach (var image in _images.Values) { image.Pe.Dispose(); image.Stream.Dispose(); } }
}
