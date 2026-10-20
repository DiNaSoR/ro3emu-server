// Turns a serialized FileDescriptorSet (the client's LocalProto/proto.pb.bytes) back into .proto files.
using System.Text;
using Google.Protobuf.Reflection;
using static Google.Protobuf.Reflection.FieldDescriptorProto.Types;
using Type = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Type;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: ProtoDump <proto.pb.bytes> <outDir>");
    return 1;
}

// Input is either a serialized FileDescriptorSet, or a directory of decompiled protoc-generated
// *Reflection.cs files whose embedded base64 descriptors are collected.
FileDescriptorSet set;
if (Directory.Exists(args[0]))
{
    set = new FileDescriptorSet();
    var call = new System.Text.RegularExpressions.Regex(@"FromBase64String\(((?:""[A-Za-z0-9+/=]*""\s*\+?\s*)+)\)");
    var piece = new System.Text.RegularExpressions.Regex(@"""([A-Za-z0-9+/=]*)""");
    foreach (var cs in Directory.EnumerateFiles(args[0], "*Reflection.cs", SearchOption.AllDirectories))
        foreach (System.Text.RegularExpressions.Match m in call.Matches(File.ReadAllText(cs)))
        {
            var b64 = string.Concat(piece.Matches(m.Groups[1].Value).Select(p => p.Groups[1].Value));
            set.File.Add(FileDescriptorProto.Parser.ParseFrom(Convert.FromBase64String(b64)));
        }
}
else
{
    set = FileDescriptorSet.Parser.ParseFrom(File.ReadAllBytes(args[0]));
}
Directory.CreateDirectory(args[1]);
int messages = 0, enums = 0, services = 0;
var definedIn = DefinedTypes(set);
var inSet = set.File.Select(f => f.Name).ToHashSet();
foreach (var file in set.File)
{
    var sb = new StringBuilder();
    sb.AppendLine($"syntax = \"{(string.IsNullOrEmpty(file.Syntax) ? "proto2" : file.Syntax)}\";");
    if (file.HasPackage) sb.AppendLine($"package {file.Package};");
    // Only the imports whose types this file names: the client's descriptors list more, and protoc warns about each
    // unused one. Custom options are not written, so the files that only declare them are never needed. A type from
    // outside the set (google.protobuf.Any) keeps the imports that are outside it too.
    var referenced = ReferencedTypes(file).ToList();
    var used = referenced.Select(t => definedIn.GetValueOrDefault(t)).ToHashSet();
    bool external = referenced.Any(t => !definedIn.ContainsKey(t));
    foreach (var dep in file.Dependency.Where(d => used.Contains(d) || (external && !inSet.Contains(d))))
        sb.AppendLine($"import \"{dep}\";");
    if (file.Options?.HasCsharpNamespace == true) sb.AppendLine($"option csharp_namespace = \"{file.Options.CsharpNamespace}\";");
    sb.AppendLine();
    bool proto3 = file.Syntax == "proto3";
    foreach (var e in file.EnumType) { WriteEnum(sb, e, 0); enums++; }
    foreach (var m in file.MessageType) { WriteMessage(sb, m, 0, proto3); messages++; }
    foreach (var s in file.Service)
    {
        services++;
        sb.AppendLine($"service {s.Name} {{");
        foreach (var r in s.Method)
            sb.AppendLine($"  rpc {r.Name} ({(r.ClientStreaming ? "stream " : "")}{Trim(r.InputType)}) returns ({(r.ServerStreaming ? "stream " : "")}{Trim(r.OutputType)});");
        sb.AppendLine("}");
        sb.AppendLine();
    }
    var path = Path.Combine(args[1], file.Name.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, sb.ToString());
}
Console.WriteLine($"{set.File.Count} files, {messages} top-level messages, {enums} top-level enums, {services} services");
return 0;

static string Trim(string typeName) => typeName.StartsWith('.') ? typeName[1..] : typeName;

// Full type name (package.Outer.Inner) -> the file that defines it.
static Dictionary<string, string> DefinedTypes(FileDescriptorSet set)
{
    var types = new Dictionary<string, string>();
    foreach (var file in set.File)
    {
        var prefix = file.HasPackage ? file.Package + "." : "";
        foreach (var e in file.EnumType) types[prefix + e.Name] = file.Name;
        foreach (var m in file.MessageType) AddMessage(m, prefix, file.Name);
    }
    return types;

    void AddMessage(DescriptorProto m, string prefix, string fileName)
    {
        types[prefix + m.Name] = fileName;
        foreach (var e in m.EnumType) types[$"{prefix}{m.Name}.{e.Name}"] = fileName;
        foreach (var n in m.NestedType) AddMessage(n, $"{prefix}{m.Name}.", fileName);
    }
}

// Full names of the message and enum types that the written fields and rpcs of a file name.
static IEnumerable<string> ReferencedTypes(FileDescriptorProto file)
{
    foreach (var t in file.MessageType.SelectMany(FieldTypes)) yield return t;
    foreach (var r in file.Service.SelectMany(s => s.Method))
    {
        yield return Trim(r.InputType);
        yield return Trim(r.OutputType);
    }

    static IEnumerable<string> FieldTypes(DescriptorProto m) => m.Field
        .Where(f => f.Type is Type.Message or Type.Enum or Type.Group)
        .Select(f => Trim(f.TypeName))
        .Concat(m.NestedType.SelectMany(FieldTypes));
}

static void WriteEnum(StringBuilder sb, EnumDescriptorProto e, int depth)
{
    var pad = new string(' ', depth * 2);
    sb.AppendLine($"{pad}enum {e.Name} {{");
    if (e.Options?.AllowAlias == true) sb.AppendLine($"{pad}  option allow_alias = true;");
    foreach (var v in e.Value) sb.AppendLine($"{pad}  {v.Name} = {v.Number};");
    sb.AppendLine($"{pad}}}");
    sb.AppendLine();
}

static void WriteMessage(StringBuilder sb, DescriptorProto m, int depth, bool proto3)
{
    var pad = new string(' ', depth * 2);
    sb.AppendLine($"{pad}message {m.Name} {{");
    var mapEntries = m.NestedType.Where(n => n.Options?.MapEntry == true).ToDictionary(n => n.Name);
    foreach (var e in m.EnumType) WriteEnum(sb, e, depth + 1);
    foreach (var n in m.NestedType.Where(n => n.Options?.MapEntry != true)) WriteMessage(sb, n, depth + 1, proto3);

    // Real oneofs only; proto3 "optional" fields live in synthetic oneofs.
    var oneofFields = m.Field.Where(f => f.HasOneofIndex && !f.Proto3Optional).GroupBy(f => f.OneofIndex).ToDictionary(g => g.Key, g => g.ToList());
    var written = new HashSet<int>();
    foreach (var f in m.Field)
    {
        if (f.HasOneofIndex && !f.Proto3Optional)
        {
            if (!written.Add(f.OneofIndex)) continue;
            sb.AppendLine($"{pad}  oneof {m.OneofDecl[f.OneofIndex].Name} {{");
            foreach (var of in oneofFields[f.OneofIndex]) sb.AppendLine($"{pad}    {FieldType(of, mapEntries)} {of.Name} = {of.Number};");
            sb.AppendLine($"{pad}  }}");
            continue;
        }
        string label = f.Label switch
        {
            Label.Repeated when IsMap(f, mapEntries) => "",
            Label.Repeated => "repeated ",
            Label.Required => "required ",
            _ when f.Proto3Optional => "optional ",
            _ when !proto3 => "optional ",
            _ => "",
        };
        var opts = new List<string>();
        if (f.HasDefaultValue) opts.Add($"default = {(f.Type == Type.String ? $"\"{f.DefaultValue}\"" : f.DefaultValue)}");
        if (f.Options?.HasPacked == true) opts.Add($"packed = {f.Options.Packed.ToString().ToLowerInvariant()}");
        var suffix = opts.Count > 0 ? $" [{string.Join(", ", opts)}]" : "";
        sb.AppendLine($"{pad}  {label}{FieldType(f, mapEntries)} {f.Name} = {f.Number}{suffix};");
    }
    foreach (var r in m.ReservedRange) sb.AppendLine($"{pad}  reserved {r.Start} to {r.End - 1};");
    sb.AppendLine($"{pad}}}");
    sb.AppendLine();
}

static bool IsMap(FieldDescriptorProto f, Dictionary<string, DescriptorProto> mapEntries)
    => f.Type == Type.Message && mapEntries.ContainsKey(Trim(f.TypeName).Split('.').Last());

static string FieldType(FieldDescriptorProto f, Dictionary<string, DescriptorProto> mapEntries)
{
    if (IsMap(f, mapEntries))
    {
        var entry = mapEntries[Trim(f.TypeName).Split('.').Last()];
        return $"map<{FieldType(entry.Field[0], mapEntries)}, {FieldType(entry.Field[1], mapEntries)}>";
    }
    return f.Type switch
    {
        Type.Message or Type.Enum or Type.Group => Trim(f.TypeName),
        Type.Double => "double",
        Type.Float => "float",
        Type.Int64 => "int64",
        Type.Uint64 => "uint64",
        Type.Int32 => "int32",
        Type.Uint32 => "uint32",
        Type.Fixed64 => "fixed64",
        Type.Fixed32 => "fixed32",
        Type.Sfixed32 => "sfixed32",
        Type.Sfixed64 => "sfixed64",
        Type.Sint32 => "sint32",
        Type.Sint64 => "sint64",
        Type.Bool => "bool",
        Type.String => "string",
        Type.Bytes => "bytes",
        _ => f.Type.ToString(),
    };
}
