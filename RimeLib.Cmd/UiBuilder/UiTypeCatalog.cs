using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace RimeLib.Cmd.UiBuilder
{
    /// <summary>
    /// The UI object model as the game's own type information describes it (the generated fb.* classes of
    /// RimeLib.Serialization.Frostbite2_0): every node type, its fields with their kinds (scalar, enum, struct,
    /// list, reference, port), every binding type, every enum with its members — and the Lua spelling VEXT gives
    /// each field (camelCase; names that are Lua keywords get a Value suffix: In → inValue, True → trueValue).
    /// Nothing here is hand-written per type, so a node the editor has never seen is still described.
    /// </summary>
    public static class UiTypeCatalog
    {
        public enum Kind { Bool, Int, Float, String, Enum, Struct, Ref, Port, PortArray, List, Unknown }

        public class FieldInfo
        {
            public string Name = "";            // C# / EBX name (DialogTitle)
            public string LuaName = "";         // dialogTitle
            public Kind Kind;
            public string TypeName = "";        // enum/struct/ref/list element type name
            public Kind ElementKind;            // for lists
            public string DeclaringType = "";   // the type that declares it (base fields come from UINodeData)
            public override string ToString() => $"{Name}:{Kind}{(TypeName != "" ? "<" + TypeName + ">" : "")}";
        }

        public class TypeInfo
        {
            public string Name = "";
            public string BaseName = "";
            public List<FieldInfo> Fields = new();     // own + inherited (base first)
            public bool IsNode, IsBinding, IsPort, IsStruct;
            public IEnumerable<FieldInfo> Ports => Fields.Where(f => f.Kind == Kind.Port);
            /// <summary>The single ports the node owns (a port reference such as JumpNode.TargetPort is not one).</summary>
            public IEnumerable<FieldInfo> OwnPorts => Ports.Where(f => !IsPortReference(Name, f.Name));
            public IEnumerable<FieldInfo> PortReferences => Ports.Where(f => IsPortReference(Name, f.Name));
            public IEnumerable<FieldInfo> PortArrays => Fields.Where(f => f.Kind == Kind.PortArray);
            public FieldInfo? Field(string p_Name) => Fields.FirstOrDefault(f => string.Equals(f.Name, p_Name, StringComparison.OrdinalIgnoreCase));
        }

        static readonly Lazy<Dictionary<string, Type>> s_FbTypes = new(LoadTypes);
        static readonly Dictionary<string, TypeInfo> s_Cache = new(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string[]> s_Enums = new(StringComparer.OrdinalIgnoreCase);

        static readonly HashSet<string> s_LuaKeywords = new() { "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "if", "in", "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while" };

        static Dictionary<string, Type> LoadTypes()
        {
            var s_Assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "RimeLib.Serialization.Frostbite2_0")
                             ?? Assembly.Load("RimeLib.Serialization.Frostbite2_0");
            var s_Types = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in s_Assembly.GetTypes().Where(t => t.Namespace == "fb" && (t.IsClass || t.IsEnum)))
                s_Types[t.Name] = t;
            return s_Types;
        }

        public static Type? FbType(string p_Name) => s_FbTypes.Value.TryGetValue(p_Name, out var t) ? t : null;

        /// <summary>VEXT's spelling of a field: first letter lower-cased; Lua keywords get the Value suffix.</summary>
        public static string LuaName(string p_Name)
        {
            if (p_Name.Length == 0) return p_Name;
            var s = char.ToLowerInvariant(p_Name[0]) + p_Name[1..];
            return s_LuaKeywords.Contains(s) ? s + "Value" : s;
        }

        static bool DerivesFrom(Type t, string p_Base)
        {
            for (var b = t; b != null; b = b.BaseType) if (b.Name == p_Base) return true;
            return false;
        }

        public static bool IsNodeType(string p_Name) => FbType(p_Name) is { } t && DerivesFrom(t, "UINodeData");
        public static bool IsBindingType(string p_Name) => FbType(p_Name) is { } t && DerivesFrom(t, "UIDataBinding");

        /// <summary>
        /// The direction of a port slot, as the shipped connections use it (measured over every ui/flow
        /// partition: In/Show/Hide/Inputs/DataInputs only ever receive, Out/True/False/Outputs only ever fire).
        /// The one rule every surface uses — model, graph view, document nodes.
        /// </summary>
        public static bool PortIsInput(string p_Field) => p_Field is "In" or "Show" or "Hide" or "Inputs" or "DataInputs";

        /// <summary>
        /// A CtrRef&lt;UINodePort&gt; that is not the node's own port but a pointer at another node's: JumpNode.TargetPort
        /// (every shipped JumpNode points at an ActionNode's In). Such a field is edited as a reference, never
        /// created as a port.
        /// </summary>
        public static bool IsPortReference(string p_Type, string p_Field) =>
            string.Equals(p_Type, "JumpNode", StringComparison.OrdinalIgnoreCase) && string.Equals(p_Field, "TargetPort", StringComparison.OrdinalIgnoreCase);

        /// <summary>All concrete node types (UINodeData descendants), most common first.</summary>
        public static IEnumerable<string> NodeTypes() =>
            s_FbTypes.Value.Values.Where(t => !t.IsAbstract && DerivesFrom(t, "UINodeData") && t.Name != "UINodeData").Select(t => t.Name).OrderBy(n => n);

        public static IEnumerable<string> BindingTypes() =>
            s_FbTypes.Value.Values.Where(t => !t.IsAbstract && DerivesFrom(t, "UIDataBinding") && t.Name != "UIDataBinding").Select(t => t.Name).OrderBy(n => n);

        public static string[] EnumMembers(string p_Enum)
        {
            if (s_Enums.TryGetValue(p_Enum, out var m)) return m;
            var t = FbType(p_Enum);
            m = t != null && t.IsEnum ? Enum.GetNames(t) : Array.Empty<string>();
            s_Enums[p_Enum] = m;
            return m;
        }

        public static TypeInfo? Describe(string p_TypeName)
        {
            if (s_Cache.TryGetValue(p_TypeName, out var s_Cached)) return s_Cached;
            var t = FbType(p_TypeName);
            if (t == null || t.IsEnum) return null;
            var s_Info = new TypeInfo
            {
                Name = t.Name, BaseName = t.BaseType?.Name ?? "",
                IsNode = DerivesFrom(t, "UINodeData"), IsBinding = DerivesFrom(t, "UIDataBinding"), IsPort = DerivesFrom(t, "UINodePort"),
            };
            s_Info.IsStruct = !DerivesFrom(t, "DataContainer") && !s_Info.IsNode;
            // base-first so the editor lists Name/ParentGraph/... before the type's own fields
            var s_Chain = new List<Type>();
            for (var b = t; b != null && b.Name != "Object" && b.Name != "DataContainer"; b = b.BaseType) s_Chain.Insert(0, b);
            foreach (var b in s_Chain)
                foreach (var p in b.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (!p.CanWrite || p.GetIndexParameters().Length > 0) continue;
                    var f = new FieldInfo { Name = p.Name, LuaName = LuaName(p.Name), DeclaringType = b.Name };
                    Classify(p.PropertyType, f);
                    s_Info.Fields.Add(f);
                }
            s_Cache[p_TypeName] = s_Info;
            return s_Info;
        }

        static void Classify(Type p_Type, FieldInfo f)
        {
            if (p_Type == typeof(bool)) { f.Kind = Kind.Bool; return; }
            if (p_Type == typeof(int) || p_Type == typeof(uint) || p_Type == typeof(short) || p_Type == typeof(ushort) || p_Type == typeof(long) || p_Type == typeof(byte) || p_Type == typeof(sbyte)) { f.Kind = Kind.Int; return; }
            if (p_Type == typeof(float) || p_Type == typeof(double)) { f.Kind = Kind.Float; return; }
            if (p_Type == typeof(string)) { f.Kind = Kind.String; return; }
            if (p_Type.IsEnum) { f.Kind = Kind.Enum; f.TypeName = p_Type.Name; return; }
            if (p_Type.IsGenericType)
            {
                var s_Def = p_Type.GetGenericTypeDefinition().Name;
                var s_Arg = p_Type.GetGenericArguments()[0];
                if (s_Def.StartsWith("CtrRef"))
                {
                    f.TypeName = s_Arg.Name;
                    f.Kind = DerivesFrom(s_Arg, "UINodePort") ? Kind.Port : Kind.Ref;
                    return;
                }
                if (s_Def.StartsWith("RefArray"))
                {
                    f.TypeName = s_Arg.Name;
                    f.Kind = DerivesFrom(s_Arg, "UINodePort") ? Kind.PortArray : Kind.List;
                    f.ElementKind = Kind.Ref;
                    return;
                }
                if (s_Def.StartsWith("List"))
                {
                    f.Kind = Kind.List; f.TypeName = s_Arg.Name;
                    var e = new FieldInfo(); Classify(s_Arg, e); f.ElementKind = e.Kind;
                    return;
                }
            }
            if (p_Type.IsClass && p_Type.Namespace == "fb") { f.Kind = Kind.Struct; f.TypeName = p_Type.Name; return; }
            f.Kind = Kind.Unknown; f.TypeName = p_Type.Name;
        }

        /// <summary>One line per field, for logs and the editor's tooltips.</summary>
        public static string Summary(string p_TypeName)
        {
            var i = Describe(p_TypeName);
            if (i == null) return p_TypeName + ": unknown type";
            var sb = new StringBuilder();
            sb.AppendLine($"{i.Name} : {i.BaseName}");
            foreach (var f in i.Fields) sb.AppendLine($"  {f.Name} ({f.LuaName}) {f.Kind}{(f.TypeName != "" ? " " + f.TypeName : "")}{(f.Kind == Kind.List ? " of " + f.ElementKind : "")}");
            return sb.ToString();
        }
    }
}
