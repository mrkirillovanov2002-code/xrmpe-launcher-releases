using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Reflection.Emit;
using System.IO.Compression;

// Static, read-only inspection: never load or execute the official assembly.
var bytes = File.ReadAllBytes(args[0]);
var signature = Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE");
int at = bytes.AsSpan().IndexOf(signature);
if (at < 8) throw new Exception("No .NET bundle");
using var input = new MemoryStream(bytes); using var reader = new BinaryReader(input);
input.Position = BitConverter.ToInt64(bytes, at - 8);
var major = reader.ReadUInt32(); reader.ReadUInt32(); int count = reader.ReadInt32(); reader.ReadString();
if (major >= 2) input.Position += 40;
var opcodes = typeof(OpCodes).GetFields().Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => unchecked((ushort)o.Value));
for (int i = 0; i < count; i++)
{
    long offset = reader.ReadInt64(), size = reader.ReadInt64(), compressed = major >= 6 ? reader.ReadInt64() : 0;
    reader.ReadByte(); var path = reader.ReadString();
    if (path != "xrmpe-launcher.dll") continue;
    byte[] assembly = bytes.AsSpan((int)offset, (int)(compressed == 0 ? size : compressed)).ToArray();
    if (compressed != 0) { using var zip = new DeflateStream(new MemoryStream(assembly), CompressionMode.Decompress); using var raw = new MemoryStream(); zip.CopyTo(raw); assembly = raw.ToArray(); }
    try
    {
        using var pe = new PEReader(new MemoryStream(assembly)); var meta = pe.GetMetadataReader();
        foreach (var typeHandle in meta.TypeDefinitions)
        {
            var type = meta.GetTypeDefinition(typeHandle); var typeName = meta.GetString(type.Name);
            var parent = type.GetDeclaringType();
            if (!parent.IsNil) typeName = meta.GetString(meta.GetTypeDefinition(parent).Name) + "/" + typeName;

            foreach (var mh in type.GetMethods())
            {
                var method = meta.GetMethodDefinition(mh); if (method.RelativeVirtualAddress == 0) continue;
                var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!; var found = new List<string>();
                var methodName = meta.GetString(method.Name);
                if (args.Length > 1 && !System.Text.RegularExpressions.Regex.IsMatch(typeName + "." + methodName, args[1])) continue;
                bool detail = true;
                if (detail) Console.WriteLine("IL " + typeName + "." + methodName + "(" + string.Join(", ", method.GetParameters().Select(h => meta.GetString(meta.GetParameter(h).Name))) + ")");
                for (int p = 0; p < il.Length;)
                {
                    ushort code = il[p++]; if (code == 254) code = (ushort)(0xfe00 | il[p++]);
                    var op = opcodes[code];
                    int n = op.OperandType switch { OperandType.InlineNone => 0, OperandType.ShortInlineI or OperandType.ShortInlineBrTarget or OperandType.ShortInlineVar => 1, OperandType.InlineVar => 2, OperandType.InlineI8 or OperandType.InlineR => 8, OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, p), _ => 4 };
                    if (op.OperandType == OperandType.InlineString) found.Add(meta.GetUserString(MetadataTokens.UserStringHandle(BitConverter.ToInt32(il, p) & 0xffffff)));
                    if (detail)
                    {
                        string operand = n == 4 ? BitConverter.ToInt32(il, p).ToString() : n == 1 ? il[p].ToString() : "";
                        if (op.OperandType == OperandType.InlineString) operand = found.Last();
                        if (op.OperandType is OperandType.InlineMethod or OperandType.InlineField)
                        {
                            var handle = MetadataTokens.EntityHandle(BitConverter.ToInt32(il, p));
                            operand = handle.Kind switch { HandleKind.MethodDefinition => meta.GetString(meta.GetMethodDefinition((MethodDefinitionHandle)handle).Name), HandleKind.MemberReference => meta.GetString(meta.GetMemberReference((MemberReferenceHandle)handle).Name), HandleKind.FieldDefinition => meta.GetString(meta.GetFieldDefinition((FieldDefinitionHandle)handle).Name), _ => handle.Kind.ToString() };
                        }
                        Console.WriteLine($"  {p:x4} {op.Name} {operand}");
                    }
                    p += n;
                }
                if (found.Count > 0) Console.WriteLine($"{path}::{typeName}.{meta.GetString(method.Name)}: {string.Join(" | ", found)}");
            }
        }
    }
    catch (BadImageFormatException) { }
}
