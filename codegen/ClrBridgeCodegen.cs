// Generates D code to call C# code using the ClrBridge library
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

// TODO: maybe provide a way to configure assembly name to D package name map?
//       if I provide any configuration options, they should probably be in a file
//       and I would need to make sure that all .NET assemblies that have been translated
//       have the SAME configuration.  I might be able to verify this somehow?


static class ClrBridgeCodegen
{
    static void Usage()
    {
        Console.WriteLine("Usage: ClrBridgeCodegen.exe <DotNetAssembly> <OutputDir>");
    }
    public static Int32 Main(String[] args)
    {
        if (args.Length != 2)
        {
            Usage();
            return 1;
        }
        String assemblyString = args[0];
        String outputDir = args[1];
        Console.WriteLine("assembly : {0}", assemblyString);
        Console.WriteLine("outputDir: {0}", outputDir);

        Assembly assembly;
        if (assemblyString.StartsWith("file:"))
            assembly = Assembly.LoadFile(Path.GetFullPath(assemblyString.Substring(5)));
        else if (assemblyString.StartsWith("gac:"))
            assembly = Assembly.Load(assemblyString.Substring(4));
        else
        {
            Console.WriteLine("Error: assembly string must start with 'file:' or 'gac:' but got '{0}'", assemblyString);
            return 1;
        }
        new Generator(assembly, outputDir).GenerateModule(assembly);
        return 0;
    }
}

class Generator
{
    readonly Assembly thisAssembly;
    readonly String outputDir;
    readonly Dictionary<Assembly, ExtraAssemblyInfo> assemblyInfoMap;
    readonly Dictionary<Type, ExtraTypeInfo> typeInfoMap;
    readonly Dictionary<String,DModule> moduleMap;
    readonly String thisAssemblyPackageName; // cached version of GetExtraAssemblyInfo(thisAssembly).packageName
    // bool lowercaseModules;

    public Generator(Assembly thisAssembly, String outputDir)
    {
        this.thisAssembly = thisAssembly;
        this.outputDir = outputDir;
        this.assemblyInfoMap = new Dictionary<Assembly,ExtraAssemblyInfo>();
        this.typeInfoMap = new Dictionary<Type,ExtraTypeInfo>();
        this.moduleMap = new Dictionary<String,DModule>();
        this.thisAssemblyPackageName = GetExtraAssemblyInfo(thisAssembly).packageName;
    }

    ExtraAssemblyInfo GetExtraAssemblyInfo(Assembly assembly)
    {
        ExtraAssemblyInfo info;
        if (!assemblyInfoMap.TryGetValue(assembly, out info))
        {
            info = new ExtraAssemblyInfo(
                assembly.GetName().Name.Replace(".", "_")
            );
            assemblyInfoMap[assembly] = info;
        }
        return info;
    }
    ExtraTypeInfo GetExtraTypeInfo(Type type)
    {
        ExtraTypeInfo info;
        if (!typeInfoMap.TryGetValue(type, out info))
        {
            info = new ExtraTypeInfo();
            typeInfoMap[type] = info;
        }
        return info;
    }

    public void GenerateModule(Assembly assembly)
    {
        // on the first pass we identify types that need to be defined inside other types
        // so that when we generate code, we can generate all the subtypes for each type
        Type[] allTypes = assembly.GetTypes();
        List<Type> rootTypes = new List<Type>();
        foreach (Type type in allTypes)
        {
            if (type.DeclaringType != null)
                GetExtraTypeInfo(type.DeclaringType).subTypes.Add(type);
            else
                rootTypes.Add(type);
        }

        foreach (Type type in rootTypes)
        {
            //writer.WriteLine("type {0}", type);

            DModule module;
            if (!moduleMap.TryGetValue(type.Namespace.NullToEmpty(), out module))
            {
                String outputDFilename = Path.Combine(outputDir,
                    Path.Combine(thisAssemblyPackageName, Util.NamespaceToModulePath(type.Namespace)));
                Console.WriteLine("[DEBUG] NewDModule '{0}'", outputDFilename);
                Directory.CreateDirectory(Path.GetDirectoryName(outputDFilename));
                StreamWriter writer = new StreamWriter(new FileStream(outputDFilename, FileMode.Create, FileAccess.Write, FileShare.Read));
                // TODO: modify if lowercaseModules
                String moduleFullName = thisAssemblyPackageName;
                if (type.Namespace.NullToEmpty().Length > 0)
                    moduleFullName = String.Format("{0}.{1}", thisAssemblyPackageName, type.Namespace);
                module = new DModule(moduleFullName, writer);
                writer.WriteLine("module {0};", moduleFullName);
                writer.WriteLine("");
                writer.WriteLine("// Keep D Symbols inside the __d struct to prevent symbol conflicts");
                writer.WriteLine("struct __d");
                writer.WriteLine("{");
                writer.WriteLine("    import cstring : CString, CStringLiteral;");
                writer.WriteLine("    static import clr;");
                writer.WriteLine("    static import clrbridge;");
                writer.WriteLine("    import clrbridge.global : globalClrBridge;");
                writer.WriteLine("    alias ObjectArray = clrbridge.Array!(clr.PrimitiveType.Object);");
                writer.WriteLine("}");
                moduleMap.Add(type.Namespace.NullToEmpty(), module);
            }
            GenerateType(module, type);
        }
        foreach (DModule module in moduleMap.Values)
        {
            module.Close();
        }
    }

    void GenerateType(DModule module, Type type)
    {
        if (type.IsValueType)
        {
            if (type.IsEnum)
            {
                Debug.Assert(!type.IsGenericType, "enum types can be generic?");
                GenerateEnum(module, type);
            }
            else
            {
                if (type.IsGenericType)
                {
                    Message(module, "skipping type {0} because generics structs aren't implemented", type.Name);
                    return;
                }
                GenerateStruct(module, type);
            }
        }
        else if (type.IsInterface)
        {
            if (type.IsGenericType)
            {
                Message(module, "skipping type {0} because generics interfaces aren't implemented", type.Name);
                return;
            }
            GenerateInterface(module, type);
        }
        else
        {
            Debug.Assert(type.IsClass);
            if (typeof(Delegate).IsAssignableFrom(type))
            {
                if (type.IsGenericType)
                {
                    Message(module, "skipping type {0} because generics delegates aren't implemented", type.Name);
                    return;
                }
                GenerateDelegate(module, type);
            }
            else
            {
                GenerateClass(module, type);
            }
        }
    }

    void Message(DModule module, String fmt, params Object[] args)
    {
        String message = String.Format(fmt, args);
        Console.WriteLine(message);
        module.WriteLine("/* {0} */", message);
    }

    void GenerateEnum(DModule module, Type type)
    {
        const String EnumValueFieldName = "value__";
        module.WriteLine("/* .NET Enum */ static struct {0}", Util.GetUnqualifiedTypeName(type));
        module.WriteLine("{");
        Type[] genericArgs = type.GetGenericArguments();
        Debug.Assert(genericArgs.IsEmpty(), "enums can have generic arguments???");
        GenerateMetadata(module, type, genericArgs);
        String baseTypeDName = ToDEquivalentType(type.BaseType); // TODO: Marshal Type instead???
        module.WriteLine("    private {0} {1}; // .NET BasteType is actually {2}", baseTypeDName, EnumValueFieldName, type.BaseType);
        module.WriteLine("    enum : typeof(this)");
        module.WriteLine("    {");
        UInt32 nonStaticFieldCount = 0;
        foreach (FieldInfo field in type.GetFields())
        {
            Debug.Assert(field.DeclaringType == type);
            Debug.Assert(field.FieldType == type);
            if (!field.IsStatic)
            {
                Debug.Assert(field.Name == EnumValueFieldName);
                nonStaticFieldCount++;
                continue;
            }
            module.WriteLine("        {0} = typeof(this)({1}({2})),", Util.ToDIdentifier(field.Name), baseTypeDName, field.GetRawConstantValue());
        }
        module.WriteLine("    }");
        Debug.Assert(nonStaticFieldCount == 1);

        // Commenting out for now because of a conflict with TypeNameKind ToString
        // It looks like C# might allow field names and method names to have the same symbol?
        // I'll need to see in which cases C# allows this, maybe only with static fields?
        // If so, the right solution would probably be to modify the field name that conflicts.
        //GenerateMethods(module, type);
        /*
        foreach (var method in type.GetMethods())
        {
            module.WriteLine("    // TODO: generate something for enum method {0}", method);
        }
        */

        // Generate opMethods so this behaves like an enum
        module.WriteLine("    typeof(this) opBinary(string op)(const typeof(this) right) const");
        module.WriteLine("    { return typeof(this)(mixin(\"this.value__ \" ~ op ~ \" right.value__\")); }");
        // TODO: there's probably more (or less) to generate to get the behavior right
        module.WriteLine("}");
    }

    void GenerateStruct(DModule module, Type type)
    {
        module.WriteLine("static struct {0}", Util.GetUnqualifiedTypeName(type));
        module.WriteLine("{");
        Type[] genericArgs = type.GetGenericArguments();
        GenerateMetadata(module, type, genericArgs);
        GenerateFields(module, type);
        GenerateMethods(module, type);
        GenerateSubTypes(module, type);
        module.WriteLine("}");
    }
    void GenerateInterface(DModule module, Type type)
    {
        module.WriteLine("interface {0}", Util.GetUnqualifiedTypeName(type));
        module.WriteLine("{");
        Debug.Assert(type.GetFields().Length == 0);
        //??? GenerateMetadata(module, type);
        GenerateMethods(module, type);
        GenerateSubTypes(module, type);
        module.WriteLine("}");
    }
    void GenerateDelegate(DModule module, Type type)
    {
        module.Write("/* .NET Delegate */ static struct {0}", Util.GetUnqualifiedTypeName(type));
        Type[] genericArgs = type.GetGenericArguments();
        GenerateGenericParameters(module, genericArgs, type.DeclaringType.GetGenericArgCount());
        module.WriteLine();
        module.WriteLine("{");
        module.WriteLine("    // TODO: generate delegate members");
        module.WriteLine("}");
    }
    void GenerateClass(DModule module, Type type)
    {
        module.Write("/* .NET class */ static struct {0}", Util.GetUnqualifiedTypeName(type));
        Type[] genericArgs = type.GetGenericArguments();
        GenerateGenericParameters(module, genericArgs, type.DeclaringType.GetGenericArgCount());
        module.WriteLine();
        module.WriteLine("{");
        module.WriteLine("    // TODO: mixin the base class rather than DotNetObject");
        module.WriteLine("    mixin __d.clrbridge.DotNetObjectMixin!\"__d.clr.DotNetObject\";");
        // generate metadata, one reason for this is so that when this type is used as a template parameter, we can
        // get the .NET name for this type
        GenerateMetadata(module, type, genericArgs);
        GenerateFields(module, type);
        GenerateMethods(module, type);
        GenerateSubTypes(module, type);
        module.WriteLine("}");
    }

    // generates a multi-line expression that creates a TypeSpec
    // assumes code starts in the middle of a line where the expression starts,
    // linePrefix is the prefix for new line of the expression
    void GenerateTypeSpecExpression(DModule module, String linePrefix, Type type)
    {
        GenerateTypeSpecExpression(module, linePrefix, type, type.GetGenericArguments());
    }
    void GenerateTypeSpecExpression(DModule module, String linePrefix, Type type, Type[] genericArgs)
    {
        if (type.IsGenericParameter)
        {
            Debug.Assert(genericArgs.IsEmpty(), "you can have a generic parameter type with generic args??");
            module.Write("__d.clrbridge.GetTypeSpec!({0})", ToDEquivalentType(type));
            return;
        }
        module.WriteLine("__d.clr.TypeSpec(");
        module.WriteLine("{0}\"{1}\",", linePrefix, type.Assembly.FullName);
        bool hasGenerics = !genericArgs.IsEmpty();
        module.Write("{0}\"{1}\"{2}", linePrefix, type.FullName, hasGenerics ? ", [" : ")", type.Name);
        if (hasGenerics)
        {
            module.WriteLine();
            foreach (Type genericArg in genericArgs)
            {
                module.WriteLine("{0}    __d.clrbridge.GetTypeSpec!({1}),", linePrefix, ToDEquivalentType(genericArg));
            }
            module.Write("{0}])", linePrefix);
        }
    }

    // TODO: remove this?
    void GenerateGenericTypeSpecsExpression(DModule module, String linePrefix, Type[] genericArgs)
    {
        if (genericArgs.IsEmpty())
            module.Write("null");
        else
        {
            module.WriteLine("[");
            foreach (Type genericArg in genericArgs)
            {
                module.WriteLine("{0}    __d.clrbridge.GetTypeSpec!({1}),", linePrefix, ToDEquivalentType(genericArg));
            }
            module.Write("{0}]", linePrefix);
        }
    }
    void GenerateParameterTypeSpecExpression(DModule module, String linePrefix, ParameterInfo[] paramInfos)
    {
        if (paramInfos.IsEmpty())
            module.Write("null");
        else
        {
            module.WriteLine("[");
            String subTypePrefix = linePrefix + "        ";
            foreach (ParameterInfo paramInfo in paramInfos)
            {
                //module.WriteLine("{0}   {1},", linePrefix, TypeSpecReference(paramInfo.ParameterType));
                module.Write("{0}    /* param '{1}' */", linePrefix, paramInfo.Name);
                GenerateTypeSpecExpression(module, subTypePrefix, paramInfo.ParameterType);
                module.WriteLine(",");
            }
            module.Write("{0}]", linePrefix);
        }
    }

    void GenerateMetadata(DModule module, Type type, Type[] genericArgs)
    {
        module.WriteLine("    static struct __clrmetadata");
        module.WriteLine("    {");
        module.Write("        enum typeSpec = ");
        GenerateTypeSpecExpression(module, "            ", type, genericArgs);
        module.WriteLine(";");
        module.WriteLine("    }");
    }

    void GenerateFields(DModule module, Type type)
    {
        foreach (FieldInfo field in type.GetFields())
        {
            Type fieldType = field.FieldType;
            String fromDll = (fieldType.Assembly == thisAssembly) ? "" :
                GetExtraAssemblyInfo(fieldType.Assembly).fromDllPrefix;
            // fields are represented as D @property functions
            module.WriteLine("    @property {0} {1}() {{ return typeof(return).init; }}; // fromPrefix '{2}' {3} {4}",
                ToDEquivalentType(fieldType),
                field.Name.ToDIdentifier(),
                fromDll,
                field.FieldType, field.FieldType.AssemblyQualifiedName);
        }
    }

    void GenerateMethods(DModule module, Type type)
    {
        foreach (ConstructorInfo constructor in type.GetConstructors())
        {
            if (type.IsValueType)
                continue; // script structs for now
            Debug.Assert(constructor.GetGenericArguments().IsEmpty(), "constructors can have generic arguments?");
            module.Write("    {0} static typeof(this) New", constructor.IsPrivate ? "private" : "public");
            ParameterInfo[] parameters = constructor.GetParameters();
            GenerateParameterList(module, parameters);
            module.WriteLine();
            module.WriteLine("    {");
            GenerateMethodBody(module, type, constructor, type, parameters);
            module.WriteLine("    }");
        }
        foreach (MethodInfo method in type.GetMethods())
        {
            //!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!
            // skip virtual methods for now so we don't get linker errors
            if (method.IsVirtual)
                continue;

            module.Write("    {0}", method.IsPrivate ? "private" : "public");
            if (method.IsStatic)
            {
                module.Write(" static");
            }
            else if (method.IsFinal)
            {
                module.Write(" final");
            }

            Type[] genericArguments = method.GetGenericArguments();
            Debug.Assert(method.ReturnType != null);
            if (method.ReturnType == typeof(void))
                module.Write(" void");
            else
                module.Write(" {0}", ToDEquivalentType(method.ReturnType));
            module.Write(" {0}", Util.ToDIdentifier(method.Name));
            ParameterInfo[] parameters = method.GetParameters();
            GenerateGenericParameters(module, genericArguments, type.GetGenericArgCount());
            GenerateParameterList(module, parameters);
            if (method.IsVirtual)
            {
                module.WriteLine(";");
                continue;
            }
            module.WriteLine();
            module.WriteLine("    {");
            GenerateMethodBody(module, type, method, method.ReturnType, parameters);
            module.WriteLine("    }");
        }
    }

    void GenerateGenericParameters(DModule module, Type[] genericArgs, UInt16 inheritedGenericArgCount)
    {
        if (genericArgs != null && inheritedGenericArgCount < genericArgs.Length)
        {
            module.Write("(");
            string prefix = "";
            // we skip inherited generic args because they will be in the containing template
            for (Int32 i = inheritedGenericArgCount; i < genericArgs.Length; i++)
            {
                Type t = genericArgs[i];
                module.Write("{0}{1}", prefix, t.Name);
                prefix = ", ";
            }
            module.Write(")");
        }
    }

    void GenerateParameterList(DModule module, ParameterInfo[] parameters)
    {
        module.Write("(");
        {
            string prefix = "";
            foreach (ParameterInfo parameter in parameters)
            {
                module.Write("{0}{1} {2}", prefix, ToDEquivalentType(parameter.ParameterType), Util.ToDIdentifier(parameter.Name));
                prefix = ", ";
            }
        }
        module.Write(")");
    }

    void GenerateMethodBody(DModule module, Type type,
        MethodBase method, Type returnType, ParameterInfo[] parameters)
    {
        // skip non-static methods for now, they just take too long right now
        if (!method.IsStatic && !method.IsConstructor)
        {
            if (returnType != typeof(void))
                module.WriteLine("        return typeof(return).init;");
            return;
        }
        // TODO: we may want to cache some of this stuff, but for now we'll just get it every time
        Type[] genericArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        module.WriteLine("        enum __method_spec__ = __d.clrbridge.MethodSpec(__clrmetadata.typeSpec, \"{0}\",", method.Name);
        module.Write    ("            /* generic args */ ");
        GenerateGenericTypeSpecsExpression(module, "            ", genericArgs);
        module.Write(", /* parameter types */ ");
        GenerateParameterTypeSpecExpression(module, "            ", parameters);
        module.WriteLine(");");

        String methodTypeString = method.IsConstructor ? "Constructor" : "Method";

        // Get Assembly so we can get Type then Method (TODO: cache this somehow?)
        module.WriteLine("        const  __this_type__ = __d.globalClrBridge.getClosedType!(__clrmetadata.typeSpec);");
        module.WriteLine("        scope (exit) __this_type__.finalRelease(__d.globalClrBridge);");
        module.WriteLine("        assert(__method_spec__.genericTypes.length == 0, \"methods with generic args not implemented\");");
        // TODO: use getClosedMethod and getClosedConstructor
        module.WriteLine("        const __method__ = __d.globalClrBridge.get{0}(__this_type__.type,", methodTypeString);
        if (!method.IsConstructor)
            module.WriteLine("            __d.CStringLiteral!\"{0}\",", method.Name);
        module.WriteLine("            __d.globalClrBridge.getTypesArray!(__method_spec__.paramTypes)());");
        module.WriteLine("        scope (exit) { __d.globalClrBridge.release(__method__); }");

        //
        // Create parameters ObjectArray
        //
        {
            uint paramIndex = 0;
            foreach (ParameterInfo parameter in parameters)
            {
                if (parameter.ParameterType.IsArray ||
                    parameter.ParameterType.IsByRef ||
                    parameter.ParameterType.IsPointer)
                {
                    // skip complicated types for now
                }
                else
                {
                    String boxType = TryGetBoxType(parameter.ParameterType);
                    if (boxType != null)
                    {
                        module.WriteLine("        auto  __param{0}__ = __d.globalClrBridge.box!(__d.clr.PrimitiveType.{1})({2}); // actual type is {3}",
                            paramIndex, boxType, Util.ToDIdentifier(parameter.Name), Util.GetQualifiedTypeName(parameter.ParameterType));
                        module.WriteLine("        scope (exit) __d.globalClrBridge.release(__param{0}__);", paramIndex);
                    }
                }
                paramIndex++;
            }
        }
        if (parameters.Length == 0)
        {
            module.WriteLine("        __d.ObjectArray __param_values__ = __d.ObjectArray.nullObject;");
        }
        else
        {
            module.WriteLine("        __d.ObjectArray __param_values__ = __d.globalClrBridge.makeObjectArray(");
            {
                uint paramIndex = 0;
                string prefix = " ";
                foreach (ParameterInfo parameter in parameters)
                {
                    if (TryGetBoxType(parameter.ParameterType) != null)
                        module.WriteLine("            {0}__param{1}__", prefix, paramIndex);
                    else
                        module.WriteLine("            {0}{1}", prefix, Util.ToDIdentifier(parameter.Name));
                    prefix = ",";
                    paramIndex++;
                }
            }
            module.WriteLine("        );");
            module.WriteLine("        scope (exit) { __d.globalClrBridge.release(__param_values__); }");
        }
        String returnValueAddrString;
        if (returnType == typeof(void))
            returnValueAddrString = "null";
        else
        {
            returnValueAddrString = "cast(void**)&__return_value__";
            if (returnType == typeof(Boolean))
                module.WriteLine("        ushort __return_value__;");
            else
                module.WriteLine("        typeof(return) __return_value__;");
        }

        if (method.IsConstructor)
        {
            module.WriteLine("        __return_value__ = cast(typeof(return))__d.globalClrBridge.callConstructor(__method__, __param_values__);");
        }
        else
        {
            module.WriteLine("        __d.globalClrBridge.funcs.CallGeneric(__method__, __d.clr.DotNetObject.nullObject, __param_values__, {0});", returnValueAddrString);
        }

        if (returnType == typeof(Boolean))
            module.WriteLine("        return (__return_value__ == 0) ? false : true;");
        else if (returnType != typeof(void))
            module.WriteLine("        return __return_value__;");
    }

    void GenerateSubTypes(DModule module, Type type)
    {
        ExtraTypeInfo typeInfo;
        if (!typeInfoMap.TryGetValue(type, out typeInfo))
            return;
        module.IncreaseDepth();
        foreach (Type subType in typeInfo.subTypes)
        {
            GenerateType(module, subType);
        }
        module.DecreaseDepth();
    }

    static String ToDMarshalType(Type type)
    {
        if (type == typeof(Boolean)) return "ushort";
        return ToDEquivalentType(type);
    }

    // TODO: add TypeContext?  like fieldDecl?  Might change const(char)* to string in some cases?
    static String ToDEquivalentType(Type type)
    {
        if (type.IsGenericParameter)
            return type.Name;

        Debug.Assert(type != typeof(void)); // not handled yet
        if (type == typeof(Boolean)) return "bool";
        if (type == typeof(Byte))    return "ubyte";
        if (type == typeof(SByte))   return "byte";
        if (type == typeof(UInt16))  return "ushort";
        if (type == typeof(Int16))   return "short";
        if (type == typeof(UInt32))  return "uint";
        if (type == typeof(Int32))   return "int";
        if (type == typeof(UInt64))  return "ulong";
        if (type == typeof(Int64))   return "long";
        if (type == typeof(Char))    return "char";
        if (type == typeof(String))  return "__d.CString";
        if (type == typeof(Single))  return "float";
        if (type == typeof(Double))  return "double";
        if (type == typeof(Decimal)) return "__d.clr.Decimal";
        if (type == typeof(Object))  return "__d.clr.DotNetObject";

        // non primitive types
        if (type == typeof(Enum)) return "__d.clrbridge.Enum"; // I think System.Enum is always 32 bits, verify this

        //String fromDll = (fieldType.Assembly == thisAssembly) ? "" :
        //    GetExtraAssemblyInfo(fieldType.Assembly).fromDllPrefix;
        return "__d.clr.DotNetObject";
    }
    static String TryGetBoxType(Type type)
    {
        // TODO: Handle type.IsGenericParameter
        //       will need to use D reflection to determine at D compile-time whether or not it's
        //       a type that needs to be boxed
        Debug.Assert(type != typeof(void)); // not handled yet
        if (type == typeof(Boolean)) return "Boolean";
        if (type == typeof(Byte))    return "Byte";
        if (type == typeof(SByte))   return "SByte";
        if (type == typeof(UInt16))  return "UInt16";
        if (type == typeof(Int16))   return "Int16";
        if (type == typeof(UInt32))  return "UInt32";
        if (type == typeof(Int32))   return "Int32";
        if (type == typeof(UInt64))  return "UInt64";
        if (type == typeof(Int64))   return "Int64";
        if (type == typeof(Char))    return "Char";
        if (type == typeof(String))  return "String";
        if (type == typeof(Single))  return "Single";
        if (type == typeof(Double))  return "Double";
        if (type == typeof(Decimal)) return "Decimal";
        if (type == typeof(Object))  return null;
        return null;
    }
}

class TabStringPool
{
    const String OneTab = "    ";
    private readonly List<String> tabList;
    public TabStringPool()
    {
        this.tabList = new List<String>();
        this.tabList.Add("");
    }
    public String this[UInt16 depth]
    {
        get
        {
            while (depth >= tabList.Count)
            {
                tabList.Add(tabList[tabList.Count-1] + OneTab);
            }
            return tabList[depth];
        }
    }
}

class DModule
{
    public readonly String fullName;
    private readonly StreamWriter writer;
    private readonly TabStringPool tabPool;
    private Boolean atMiddleOfLine;
    private UInt16 depth;
    public DModule(String fullName, StreamWriter writer)
    {
        this.fullName = fullName;
        this.writer = writer;
        this.tabPool = new TabStringPool();
        this.atMiddleOfLine = false;
    }
    public void IncreaseDepth() { this.depth += 1; }
    public void DecreaseDepth() { this.depth -= 1; }
    public void Close() { writer.Close(); }
    void AboutToWrite()
    {
        if (!atMiddleOfLine)
        {
            writer.Write(tabPool[depth]);
            atMiddleOfLine = true;
        }
    }
    public void Write(String msg)
    {
        AboutToWrite();
        writer.Write(msg);
    }
    public void Write(String fmt, params Object[] args)
    {
        AboutToWrite();
        writer.Write(fmt, args);
    }
    public void WriteLine()
    {
        // don't call AboutToWrite because we don't want line prefix on empty lines
        writer.WriteLine();
        atMiddleOfLine = false;
    }
    public void WriteLine(String msg)
    {
        AboutToWrite();
        writer.WriteLine(msg);
        atMiddleOfLine = false;
    }
    public void WriteLine(String fmt, params Object[] args)
    {
        AboutToWrite();
        writer.WriteLine(fmt, args);
        atMiddleOfLine = false;
    }
}

class ExtraAssemblyInfo
{
    public readonly string packageName;
    public readonly string fromDllPrefix;
    public ExtraAssemblyInfo(string packageName)
    {
        this.packageName = packageName;
        this.fromDllPrefix = String.Format("fromDll!\"{0}\".", packageName);
    }
}

class ExtraTypeInfo
{
    public readonly List<Type> subTypes; // types that are declared inside this type
    public ExtraTypeInfo()
    {
        this.subTypes = new List<Type>();
    }
}

static class Util
{
    public static String NamespaceToModulePath(String @namespace)
    {
        String path = "";
        if (@namespace != null)
        {
            foreach (String part in @namespace.Split('.'))
            {
                path = Path.Combine(path, part);
            }
        }
        return Path.Combine(path, "package.d");
    }
    // add a trailing '_' to keywords
    public static String ToDIdentifier(this String s)
    {
        if (s == "align") return "align_";
        if (s == "module") return "module_";
        if (s == "version") return "version_";
        if (s == "function") return "function_";
        if (s == "scope") return "scope_";
        if (s == "asm") return "asm_";
        if (s == "lazy") return "lazy_";
        return s
            .Replace("$", "_")
            .Replace("<", "_")
            .Replace(">", "_")
            .Replace("=", "_")
            .Replace("`", "_");
    }
    // rename types that conflict with standard D types
    public static String GetQualifiedTypeName(Type type)
    {
        String prefix = (type.DeclaringType == null) ? "" : GetQualifiedTypeName(type.DeclaringType) + ".";
        return prefix + GetUnqualifiedTypeName(type);
    }
    public static String GetUnqualifiedTypeName(Type type)
    {
        if (type.Name == "Object")
            return "DotNetObject";
        if (type.Name == "Exception")
            return "DotNetException";
        if (type.Name == "TypeInfo")
            return "DotNetTypeInfo";
        return type.Name
            .Replace("$", "_")
            .Replace("<", "_")
            .Replace(">", "_")
            .Replace("=", "_")
            .Replace("`", "_");
    }
}

static class Extensions
{
    public static String NullToEmpty(this String s)
    {
       return (s == null) ? "" : s;
    }
    public static Boolean IsEmpty<T>(this T[] array) { return array == null || array.Length == 0; }
    public static UInt16 ToUInt16(this Int32 value)
    {
        if (value > (Int32)UInt16.MaxValue)
            throw new Exception(String.Format("Cannot convert value {0} of type Int32 to UInt16 because it is too big", value));
        return (UInt16)value;
    }
    public static UInt16 GetGenericArgCount(this Type type)
    {
        if (type == null)
            return 0;
        Type[] genericArgs = type.GetGenericArguments();
        return (genericArgs == null) ? (UInt16)0 : genericArgs.Length.ToUInt16();
    }
}
