using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace Mutate4CSharp;

internal sealed class CecilSignatureNames : ISignatureTypeProvider<string, object?>
{
    internal static string TypeName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var parent = type.GetDeclaringType();
        var name = reader.GetString(type.Name);
        return !parent.IsNil ? TypeName(reader, parent) + "/" + name :
            (reader.GetString(type.Namespace) is { Length: > 0 } ns ? ns + "." : "") + name;
    }
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "System.Boolean", PrimitiveTypeCode.Byte => "System.Byte",
        PrimitiveTypeCode.Char => "System.Char", PrimitiveTypeCode.Double => "System.Double",
        PrimitiveTypeCode.Int16 => "System.Int16", PrimitiveTypeCode.Int32 => "System.Int32",
        PrimitiveTypeCode.Int64 => "System.Int64", PrimitiveTypeCode.IntPtr => "System.IntPtr",
        PrimitiveTypeCode.Object => "System.Object", PrimitiveTypeCode.SByte => "System.SByte",
        PrimitiveTypeCode.Single => "System.Single", PrimitiveTypeCode.String => "System.String",
        PrimitiveTypeCode.UInt16 => "System.UInt16", PrimitiveTypeCode.UInt32 => "System.UInt32",
        PrimitiveTypeCode.UInt64 => "System.UInt64", PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
        PrimitiveTypeCode.Void => "System.Void", _ => throw new NotSupportedException()
    };
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => TypeName(reader, handle);
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var type = reader.GetTypeReference(handle);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference) throw new NotSupportedException();
        return (reader.GetString(type.Namespace) is { Length: > 0 } ns ? ns + "." : "") + reader.GetString(type.Name);
    }
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetArrayType(string elementType, ArrayShape shape) => throw new NotSupportedException();
    public string GetFunctionPointerType(MethodSignature<string> signature) => throw new NotSupportedException();
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => throw new NotSupportedException();
    public string GetGenericMethodParameter(object? context, int index) => throw new NotSupportedException();
    public string GetGenericTypeParameter(object? context, int index) => throw new NotSupportedException();
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => throw new NotSupportedException();
    public string GetPinnedType(string elementType) => throw new NotSupportedException();
    public string GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, context);
}
