using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using Dalamud.Plugin.Services;

namespace Underpaint.Internal;

internal sealed class VfxEditorBridge(IPluginLog log) : IDisposable
{
    private const string AssemblyName = "VFXEditorCN";
    private static readonly Version SupportedVersion = new(1, 9, 6, 0);
    private string? documentWriteLocation;
    private string? documentVfxPath;

    internal string CreateAnimatedDecalRing()
    {
        var assembly = FindAssembly();
        var plugin = RequireType(assembly, "VfxEditor.Plugin");
        var group = GetStatic(plugin, "AvfxManager");
        var manager = First(GetList(group, "Managers"));
        Invoke(manager, "AddDocument");
        var document = Get(manager, "ActiveDocument");

        try
        {
            Invoke(document, "OpenTemplate", "default_vfx.avfx");
            var file = Get(document, "File");
            BuildAnimatedDecalRing(assembly, plugin, file);

            var path = $"vfx/common/eff/underpaint_decal_ring_{Guid.NewGuid():N}.avfx";
            SetReplacement(assembly, document, path);
            documentVfxPath = path;
            var bytes = (byte[])Invoke(file, "ToBytes");
            documentWriteLocation = (string)Get(document, "WriteLocation");
            Directory.CreateDirectory(Path.GetDirectoryName(documentWriteLocation)!);
            File.WriteAllBytes(documentWriteLocation, bytes);
            Invoke(file, "Update");
            Invoke(manager, "Show");
            return path;
        }
        catch
        {
            Invoke(manager, "RemoveDocument", document, true);
            throw;
        }
    }

    public void Dispose()
    {
        if (documentVfxPath == null)
            return;

        try
        {
            var assembly = FindAssembly();
            var plugin = RequireType(assembly, "VfxEditor.Plugin");
            var group = GetStatic(plugin, "AvfxManager");
            foreach (var manager in GetList(group, "Managers"))
            {
                foreach (var document in GetList(manager!, "Documents"))
                {
                    if ((string)Get(document!, "ReplacePath") != documentVfxPath)
                        continue;
                    Invoke(manager!, "RemoveDocument", document!, true);
                    documentWriteLocation = null;
                    documentVfxPath = null;
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            log.Warning(exception, "[Underpaint] Failed to close the VFXEditorCN decal-ring document.");
        }
    }

    private static void BuildAnimatedDecalRing(Assembly assembly, Type plugin, object file)
    {
        var main = Get(file, "Main");
        Clear(main, "Schedulers");
        Clear(main, "Timelines");
        Clear(main, "Emitters");
        Clear(main, "Particles");
        Clear(main, "Effectors");
        Clear(main, "Binders");
        Clear(main, "Textures");
        Clear(main, "Models");

        var root = (string)GetStatic(plugin, "RootLocation");
        Invoke(file, "Import", Path.Combine(root, "Files", "default_particle.vfxedit"));
        Invoke(file, "Import", Path.Combine(root, "Files", "default_emitter.vfxedit"));
        Invoke(file, "Import", Path.Combine(root, "Files", "default_timeline.vfxedit"));

        var particle = Last(GetList(main, "Particles"));
        var emitter = Last(GetList(main, "Emitters"));
        var timeline = Last(GetList(main, "Timelines"));
        ConfigureParticle(assembly, particle);
        ConnectEmitter(assembly, emitter, particle);
        ConnectTimeline(assembly, timeline, emitter);
        AddScheduler(assembly, file, main, timeline);
        Invoke(file, "OnChange");
    }

    private static void ConfigureParticle(Assembly assembly, object particle)
    {
        SetLiteral(particle, "Type", 11);
        Invoke(particle, "UpdateData");
        SetLiteral(particle, "LoopStart", 0);
        SetLiteral(particle, "LoopEnd", 20);
        SetField(Get(particle, "Life"), "Enabled", false);

        var data = Invoke(particle, "GetData");
        SetLiteral(data, "ScalingScale", 1f);
        SetLiteral(data, "RingFan", 1f);
        SetCurve(assembly, Get(data, "Width"), [(0, 0.35f)], false);

        var color = Get(particle, "Color");
        SetCurve(assembly, Get(color, "Bri"), [(0, 1f), (10, 4f), (20, 1f)], true);
    }

    private static void ConnectEmitter(Assembly assembly, object emitter, object particle)
    {
        SetField(Get(emitter, "Life"), "Enabled", false);
        var items = GetList(emitter, "Particles");
        items.Clear();
        var item = Create(assembly, "VfxEditor.AvfxFormat.AvfxEmitterItem", true, emitter, true);
        Invoke(Get(item, "ParticleSelect"), "Select", particle);
        SetLiteral(item, "Enabled", true);
        items.Add(item);
    }

    private static void ConnectTimeline(Assembly assembly, object timeline, object emitter)
    {
        SetLiteral(timeline, "LoopStart", 0);
        SetLiteral(timeline, "LoopEnd", 20);
        var items = GetList(timeline, "Items");
        items.Clear();
        var item = Create(assembly, "VfxEditor.AvfxFormat.AvfxTimelineItem", timeline, true);
        Invoke(Get(item, "EmitterSelect"), "Select", emitter);
        SetLiteral(item, "Enabled", true);
        SetLiteral(item, "StartTime", 0);
        SetLiteral(item, "EndTime", 20);
        items.Add(item);
    }

    private static void AddScheduler(Assembly assembly, object file, object main, object timeline)
    {
        var scheduler = Create(assembly, "VfxEditor.AvfxFormat.AvfxScheduler", file, Get(file, "NodeGroupSet"));
        GetList(main, "Schedulers").Add(scheduler);
        var item = Create(assembly, "VfxEditor.AvfxFormat.AvfxSchedulerItem", scheduler, "Item", true);
        Invoke(Get(item, "TimelineSelect"), "Select", timeline);
        GetList(scheduler, "Items").Add(item);
    }

    private static void SetCurve(Assembly assembly, object curve, (int Time, float Value)[] points, bool repeat)
    {
        Invoke(curve, "SetAssigned", true, false);
        SetLiteral(curve, "PreBehavior", 0);
        SetLiteral(curve, "PostBehavior", repeat ? 1 : 0);
        var keys = GetList(curve, "Keys");
        keys.Clear();
        var keyType = RequireType(assembly, "VfxEditor.AvfxFormat.Enums+KeyType");
        var linear = Enum.ToObject(keyType, 1);
        foreach (var point in points)
        {
            keys.Add(
                Create(
                    assembly,
                    "VfxEditor.AvfxFormat.AvfxCurveKey",
                    curve,
                    linear,
                    point.Time,
                    0f,
                    0f,
                    point.Value
                )
            );
        }
        Invoke(curve, "Cleanup");
    }

    private static void SetReplacement(Assembly assembly, object document, string path)
    {
        var resultType = RequireType(assembly, "VfxEditor.Select.SelectResult");
        var enumType = RequireType(assembly, "VfxEditor.Select.SelectResultType");
        var gamePath = Enum.ToObject(enumType, 1);
        var result = Activator.CreateInstance(resultType, gamePath, "Underpaint", "[Underpaint] Decal Ring", path)
            ?? throw new InvalidOperationException("VFXEditorCN did not create a replacement descriptor.");
        Invoke(document, "SetReplace", result);
    }

    private static Assembly FindAssembly()
    {
        var assembly = AssemblyLoadContext.All
            .SelectMany(context => context.Assemblies)
            .FirstOrDefault(candidate => candidate.GetName().Name == AssemblyName);
        if (assembly == null)
            throw new InvalidOperationException("VFXEditorCN 1.9.6.0 must be installed and enabled.");
        if (assembly.GetName().Version != SupportedVersion)
            throw new InvalidOperationException(
                $"Unsupported VFXEditorCN version {assembly.GetName().Version}; expected {SupportedVersion}."
            );
        return assembly;
    }

    private static Type RequireType(Assembly assembly, string name) =>
        assembly.GetType(name, true) ?? throw new MissingMemberException(assembly.FullName, name);

    private static object Create(Assembly assembly, string typeName, params object[] arguments) =>
        Activator.CreateInstance(RequireType(assembly, typeName), arguments)
        ?? throw new InvalidOperationException($"VFXEditorCN did not create {typeName}.");

    private static object GetStatic(Type type, string name)
    {
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (property != null)
            return property.GetValue(null) ?? throw new InvalidOperationException($"VFXEditorCN {name} is not initialized.");
        var field = FindField(type, name, BindingFlags.Static);
        return field.GetValue(null) ?? throw new InvalidOperationException($"VFXEditorCN {name} is not initialized.");
    }

    private static object Get(object target, string name)
    {
        var type = target.GetType();
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (property != null)
            return property.GetValue(target) ?? throw new InvalidOperationException($"VFXEditorCN {name} is null.");
        var field = FindField(type, name, BindingFlags.Instance);
        return field.GetValue(target) ?? throw new InvalidOperationException($"VFXEditorCN {name} is null.");
    }

    private static FieldInfo FindField(Type type, string name, BindingFlags scope)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var field = current.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | scope | BindingFlags.DeclaredOnly);
            if (field != null)
                return field;
        }
        throw new MissingFieldException(type.FullName, name);
    }

    private static void SetField(object target, string name, object value) =>
        FindField(target.GetType(), name, BindingFlags.Instance).SetValue(target, value);

    private static void SetLiteral(object target, string name, object value)
    {
        var literal = Get(target, name);
        var property = literal.GetType().GetProperty("Value")
            ?? throw new MissingMemberException(literal.GetType().FullName, "Value");
        var converted = property.PropertyType.IsEnum
            ? Enum.ToObject(property.PropertyType, value)
            : Convert.ChangeType(value, property.PropertyType);
        property.SetValue(literal, converted);
    }

    private static object Invoke(object target, string name, params object[] arguments)
    {
        var method = target.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(candidate => candidate.Name == name && ParametersMatch(candidate, arguments))
            ?? throw new MissingMethodException(target.GetType().FullName, name);
        return method.Invoke(target, arguments) ?? new object();
    }

    private static bool ParametersMatch(MethodInfo method, object[] arguments)
    {
        var parameters = method.GetParameters();
        if (parameters.Length != arguments.Length)
            return false;
        for (var index = 0; index < parameters.Length; index++)
        {
            if (arguments[index] != null && !parameters[index].ParameterType.IsInstanceOfType(arguments[index]))
                return false;
        }
        return true;
    }

    private static IList GetList(object target, string name) => (IList)Get(target, name);
    private static object First(IList list) => list.Count > 0 ? list[0]! : throw new InvalidOperationException("VFXEditorCN has no manager.");
    private static object Last(IList list) =>
        list.Count > 0 ? list[list.Count - 1]! : throw new InvalidOperationException("VFXEditorCN import produced no node.");
    private static void Clear(object target, string name) => GetList(target, name).Clear();
}
