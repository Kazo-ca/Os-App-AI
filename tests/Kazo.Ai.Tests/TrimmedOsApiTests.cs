using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Kazo.Ai.Tests;

/// <summary>
/// The published Kazo OS trims the .NET framework, and its Store refuses an app that calls a member
/// the trim removed (ADR 0242 of Kazo OS). Version 0.1.1 was refused for these calls; this test keeps
/// them out. The complete check runs when the OS installs the package.
/// </summary>
public sealed class TrimmedOsApiTests
{
    private static readonly (string Type, string Member)[] Removed =
    [
        ("System.Linq.Enumerable", "Sum"),
        ("System.Threading.Channels.ChannelWriter`1", "WriteAsync"),
        ("System.Text.RegularExpressions.Match", "NextMatch"),
    ];

    [Fact]
    public void The_app_does_not_call_framework_members_the_trimmed_os_removed()
    {
        using FileStream file = File.OpenRead(typeof(Kazo.Ai.AiApp).Assembly.Location);
        using PEReader pe = new(file);
        MetadataReader reader = pe.GetMetadataReader();
        List<string> found = [];

        foreach (TypeReferenceHandle handle in reader.TypeReferences)
        {
            if (Name(reader, handle) == "System.Text.RegularExpressions.MatchEvaluator")
            {
                found.Add("MatchEvaluator");
            }
        }

        foreach (MemberReferenceHandle handle in reader.MemberReferences)
        {
            MemberReference member = reader.GetMemberReference(handle);
            string? type = member.Parent.Kind switch
            {
                HandleKind.TypeReference => Name(reader, (TypeReferenceHandle)member.Parent),
                HandleKind.TypeSpecification => GenericDefinition(reader, (TypeSpecificationHandle)member.Parent),
                _ => null,
            };
            string name = reader.GetString(member.Name);
            BlobReader signature = reader.GetBlobReader(member.Signature);
            SignatureHeader header = signature.ReadSignatureHeader();
            if (header.IsGeneric)
            {
                signature.ReadCompressedInteger();
            }

            int parameters = header.Kind == SignatureKind.Method ? signature.ReadCompressedInteger() : 0;
            if (Removed.Contains((type ?? string.Empty, name)))
            {
                found.Add($"{type}.{name}");
            }

            // Kept: instance Replace(string, string). Removed: the static overloads and the evaluator ones.
            if (type == "System.Text.RegularExpressions.Regex" && (name == "Replace" && (!header.IsInstance || parameters != 2)
                || name == "Match" && header.IsInstance && parameters != 1))
            {
                found.Add($"{type}.{name}({parameters})");
            }
        }

        Assert.Empty(found);
    }

    private static string Name(MetadataReader reader, TypeReferenceHandle handle)
    {
        TypeReference type = reader.GetTypeReference(handle);
        string ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? reader.GetString(type.Name) : ns + "." + reader.GetString(type.Name);
    }

    private static string? GenericDefinition(MetadataReader reader, TypeSpecificationHandle handle)
    {
        BlobReader blob = reader.GetBlobReader(reader.GetTypeSpecification(handle).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
        {
            return null;
        }

        blob.ReadSignatureTypeCode();
        EntityHandle definition = blob.ReadTypeHandle();
        return definition.Kind == HandleKind.TypeReference ? Name(reader, (TypeReferenceHandle)definition) : null;
    }
}
