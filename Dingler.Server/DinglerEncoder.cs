extern alias HexGame;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using HexGame::Game.Shared.Network;
using HexGame::GPG.Core.MemHelpers;

namespace Dingler.Server;

public sealed class DinglerEncoder
{
    private readonly ObjFmt.Encoder _encoder = ObjFmt.MakeEncoder(PickTypeWithSwap, CheckProp);
    // One encoder per session, but several threads use it (the request handlers, the message sender, pushes from games and
    // chat). It is not thread-safe: two overlapping encodes crashed a login with an index error and ended the connection.
    private readonly object _encodeGate = new();
    private static readonly ConcurrentDictionary<MemberInfo, Lazy<bool>> SerializationCache = new();
    private static readonly ConcurrentDictionary<Type, Lazy<string>> DescriptionCache = new();
    private static readonly Dictionary<Type, Type> TypeReplacements = new();
    private static readonly Pooler<PooledMemoryStream>.Synced MemPool = new(0);

    public static void RegisterTypeSwap<TSource, TReplacement>()
    {
        TypeReplacements[typeof(TSource)] = typeof(TReplacement);
    }

    private static string PickTypeWithSwap(Type? expected, object val)
    {
        // none of the hex dlls use nullable, so this is just an accepted warning
        if (val is null)
        {
            if (TypeReplacements.TryGetValue(expected, out var typeToReplace))
                return DescribeTypes(typeToReplace);

            return DescribeTypes(expected);
        }

        var concreteType = val.GetType();
        if (TypeReplacements.TryGetValue(concreteType, out var concreteTypeToReplace))
            return DescribeTypes(concreteTypeToReplace);

        return DescribeTypes(concreteType);
    }

    // Note to Future me: leave this alone. Hex's data member attribute and this one are different. We have to match on
    // the name.
    private static bool CheckProp(MemberInfo memberInfo)
    {
        return SerializationCache.GetOrAdd(memberInfo, static m => new Lazy<bool>(() =>
        {
            var allAttrs = m.GetCustomAttributes();
            return allAttrs.Any(attr => attr.GetType().Name == "DataMemberAttribute");
        })).Value;
        
    }

    private static string DescribeTypes(Type type)
    {
        return DescriptionCache.GetOrAdd(type, static k => new Lazy<string>(() => BuildDescription(k))).Value;
    }

    private static string BuildDescription(Type type)
    {
        var b = new StringBuilder();

        BuildDescriptionInternal(type, b);

        return b.ToString();
    }

    private static void BuildDescriptionInternal(Type type, StringBuilder builder)
    {
        if (type.IsGenericType)
        {
            builder.Append(type.Namespace);
            builder.Append(".");
            
            if (type.IsNested)
            {
                builder.Append(type.DeclaringType?.Name);
                builder.Append("+");
            }

            builder.Append(type.Name);
            Type[] genericArguments = type.GetGenericArguments();
            builder.Append("#");
            BuildDescriptionInternal(genericArguments[0], builder);
            for (int i = 1; i < genericArguments.Length; i++)
            {
                builder.Append("!");
                BuildDescriptionInternal(genericArguments[i], builder);
            }
        }
        else
        {
            builder.Append(type.FullName);
        }
    }

    public byte[] Encode(object data)
    {
        using PooledMemoryStream pooledMemoryStream = MemPool.Get();
        lock (_encodeGate)
        {
            _encoder.Encode(pooledMemoryStream.Stream, null, data, null, data.GetType());
        }
        return pooledMemoryStream.Stream.ToArray();
    }
}