using System.Collections.Concurrent;
using System.Reflection;
using Astropiano.ReflectionTools;

namespace Ramphastos.Core;

/// <summary>
/// Compiled member accessors for view-model properties. Creation is once per
/// member; access goes through Astropiano.ReflectionTools IL-emitting
/// reader/writer types (the differ's hot path — §6.5).
/// </summary>
internal static class MemberAccess
{
    private static readonly ConcurrentDictionary<PropertyInfo, IMemberReader> Readers = new();
    private static readonly ConcurrentDictionary<PropertyInfo, IMemberWriter> Writers = new();

    public static IMemberReader ReaderFor(PropertyInfo prop) =>
        Readers.GetOrAdd(prop, static p =>
            (IMemberReader)Activator.CreateInstance(
                typeof(PropertyReader<,>).MakeGenericType(p.DeclaringType!, p.PropertyType),
                p.DeclaringType!, p)!);

    public static IMemberWriter WriterFor(PropertyInfo prop) =>
        Writers.GetOrAdd(prop, static p =>
            (IMemberWriter)Activator.CreateInstance(
                typeof(PropertyWriter<,>).MakeGenericType(p.DeclaringType!, p.PropertyType),
                p.DeclaringType!, p)!);
}
