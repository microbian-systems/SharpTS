using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    // TypedArray type definitions
    private TypeBuilder? _typedArrayBaseType;
    private FieldBuilder? _typedArrayBufferField;
    private FieldBuilder? _typedArrayByteOffsetField;
    private FieldBuilder? _typedArrayLengthField;
    private FieldBuilder? _typedArrayArrayBufferField;
    private MethodBuilder? _typedArrayBytesPerElementGetter;

    /// <summary>
    /// Emits all TypedArray types for standalone DLLs.
    /// </summary>
    private void EmitTypedArrayTypes(ModuleBuilder module, EmittedRuntime runtime)
    {
        // First emit the base class
        EmitTypedArrayBaseType(module, runtime);

        // Then emit concrete types
        EmitConcreteTypedArrayType(module, runtime, "Int8Array", 1, true, false);
        EmitConcreteTypedArrayType(module, runtime, "Uint8Array", 1, false, false);
        EmitConcreteTypedArrayType(module, runtime, "Uint8ClampedArray", 1, false, true);
        EmitConcreteTypedArrayType(module, runtime, "Int16Array", 2, true, false);
        EmitConcreteTypedArrayType(module, runtime, "Uint16Array", 2, false, false);
        EmitConcreteTypedArrayType(module, runtime, "Int32Array", 4, true, false);
        EmitConcreteTypedArrayType(module, runtime, "Uint32Array", 4, false, false);
        EmitConcreteTypedArrayType(module, runtime, "Float32Array", 4, false, false, isFloat: true);
        EmitConcreteTypedArrayType(module, runtime, "Float64Array", 8, false, false, isFloat: true);
        EmitConcreteTypedArrayType(module, runtime, "BigInt64Array", 8, true, false, isBigInt: true);
        EmitConcreteTypedArrayType(module, runtime, "BigUint64Array", 8, false, false, isBigInt: true);

        // Finalize base type after all derived types are defined
        _typedArrayBaseType!.CreateType();
    }

    /// <summary>
    /// Emits the abstract $TypedArray base class.
    /// </summary>
    private void EmitTypedArrayBaseType(ModuleBuilder module, EmittedRuntime runtime)
    {
        _typedArrayBaseType = module.DefineType(
            "$TypedArray",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Class,
            _types.Object
        );
        runtime.TypedArrayBaseType = _typedArrayBaseType;

        // Fields
        _typedArrayBufferField = _typedArrayBaseType.DefineField("_buffer", typeof(byte[]), FieldAttributes.Family);
        _typedArrayByteOffsetField = _typedArrayBaseType.DefineField("_byteOffset", _types.Int32, FieldAttributes.Family);
        _typedArrayLengthField = _typedArrayBaseType.DefineField("_length", _types.Int32, FieldAttributes.Family);
        _typedArrayArrayBufferField = _typedArrayBaseType.DefineField("_arrayBuffer", _types.Object, FieldAttributes.Family);

        // Abstract properties
        _typedArrayBytesPerElementGetter = EmitTypedArrayAbstractProperty(_typedArrayBaseType, "BytesPerElement", _types.Int32);
        EmitTypedArrayAbstractProperty(_typedArrayBaseType, "TypeName", _types.String);
        runtime.TypedArrayElementGet = _typedArrayBaseType.DefineMethod(
            "Get",
            MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            _types.Object,
            [_types.Int32]
        );
        runtime.TypedArrayElementSet = _typedArrayBaseType.DefineMethod(
            "Set",
            MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            _types.Void,
            [_types.Int32, _types.Object]
        );

        // Concrete properties: Length, ByteOffset, ByteLength, Buffer
        EmitTypedArrayLengthProperty(_typedArrayBaseType, runtime);
        EmitTypedArrayByteOffsetProperty(_typedArrayBaseType, runtime);
        EmitTypedArrayByteLengthProperty(_typedArrayBaseType, runtime);
        EmitTypedArrayBufferProperty(_typedArrayBaseType, runtime);

        // Protected constructor
        var baseCtor = _typedArrayBaseType.DefineConstructor(
            MethodAttributes.Family,
            CallingConventions.Standard,
            [typeof(byte[]), _types.Int32, _types.Int32, _types.Object]
        );
        runtime.TypedArrayBaseCtor = baseCtor;

        var il = baseCtor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _types.GetDefaultConstructor(_types.Object));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, _typedArrayBufferField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Stfld, _typedArrayByteOffsetField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Stfld, _typedArrayLengthField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg, 4);
        il.Emit(OpCodes.Stfld, _typedArrayArrayBufferField);
        il.Emit(OpCodes.Ret);

        // GetBuffer method for internal access
        var getBufferMethod = _typedArrayBaseType.DefineMethod(
            "GetBuffer",
            MethodAttributes.Public,
            typeof(byte[]),
            Type.EmptyTypes
        );
        runtime.TypedArrayGetBuffer = getBufferMethod;
        var getBufferIl = getBufferMethod.GetILGenerator();
        getBufferIl.Emit(OpCodes.Ldarg_0);
        getBufferIl.Emit(OpCodes.Ldfld, _typedArrayBufferField);
        getBufferIl.Emit(OpCodes.Ret);
    }

    private MethodBuilder EmitTypedArrayAbstractProperty(TypeBuilder typeBuilder, string name, Type returnType)
    {
        var prop = typeBuilder.DefineProperty(name, PropertyAttributes.None, returnType, Type.EmptyTypes);
        var getter = typeBuilder.DefineMethod(
            $"get_{name}",
            MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            returnType,
            Type.EmptyTypes
        );
        prop.SetGetMethod(getter);
        return getter;
    }

    private void EmitTypedArrayLengthProperty(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var prop = typeBuilder.DefineProperty("Length", PropertyAttributes.None, _types.Int32, Type.EmptyTypes);
        var getter = typeBuilder.DefineMethod(
            "get_Length",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            _types.Int32,
            Type.EmptyTypes
        );
        runtime.TypedArrayLengthGetter = getter;
        var il = getter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _typedArrayLengthField!);
        il.Emit(OpCodes.Ret);
        prop.SetGetMethod(getter);
    }

    private void EmitTypedArrayByteOffsetProperty(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var prop = typeBuilder.DefineProperty("ByteOffset", PropertyAttributes.None, _types.Int32, Type.EmptyTypes);
        var getter = typeBuilder.DefineMethod(
            "get_ByteOffset",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            _types.Int32,
            Type.EmptyTypes
        );
        runtime.TypedArrayByteOffsetGetter = getter;
        var il = getter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _typedArrayByteOffsetField!);
        il.Emit(OpCodes.Ret);
        prop.SetGetMethod(getter);
    }

    private void EmitTypedArrayByteLengthProperty(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var prop = typeBuilder.DefineProperty("ByteLength", PropertyAttributes.None, _types.Int32, Type.EmptyTypes);
        var getter = typeBuilder.DefineMethod(
            "get_ByteLength",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig | MethodAttributes.Virtual,
            _types.Int32,
            Type.EmptyTypes
        );
        runtime.TypedArrayByteLengthGetter = getter;
        var il = getter.GetILGenerator();
        // return _length * BytesPerElement
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _typedArrayLengthField!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _typedArrayBytesPerElementGetter!);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Ret);
        prop.SetGetMethod(getter);
    }

    private void EmitTypedArrayBufferProperty(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var prop = typeBuilder.DefineProperty("Buffer", PropertyAttributes.None, _types.Object, Type.EmptyTypes);
        var getter = typeBuilder.DefineMethod(
            "get_Buffer",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            _types.Object,
            Type.EmptyTypes
        );
        runtime.TypedArrayBufferGetter = getter;
        var il = getter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _typedArrayArrayBufferField!);
        il.Emit(OpCodes.Ret);
        prop.SetGetMethod(getter);
    }

    /// <summary>
    /// Emits a concrete TypedArray type (e.g., $Uint8Array).
    /// </summary>
    private void EmitConcreteTypedArrayType(
        ModuleBuilder module,
        EmittedRuntime runtime,
        string name,
        int bytesPerElement,
        bool signed,
        bool clamped,
        bool isFloat = false,
        bool isBigInt = false)
    {
        var typeBuilder = module.DefineType(
            $"${name}",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
            _typedArrayBaseType
        );

        // Store type reference in runtime
        StoreTypedArrayType(runtime, name, typeBuilder);

        // Override BytesPerElement
        EmitBytesPerElementProperty(typeBuilder, bytesPerElement);

        // Override TypeName
        EmitTypeNameProperty(typeBuilder, name);

        // Constructor: public $Uint8Array(int length)
        var lengthCtor = EmitTypedArrayLengthConstructor(typeBuilder, runtime, bytesPerElement);
        StoreTypedArrayLengthCtor(runtime, name, lengthCtor);

        // Constructor: public $Uint8Array(object buffer, int byteOffset, int? length)
        var bufferCtor = EmitTypedArrayBufferConstructor(typeBuilder, runtime, bytesPerElement);
        StoreTypedArrayBufferCtor(runtime, name, bufferCtor);

        // Indexer: public object this[int index] { get; set; }
        EmitTypedArrayIndexer(typeBuilder, runtime, bytesPerElement, signed, clamped, isFloat, isBigInt);

        // Finalize type
        typeBuilder.CreateType();
    }

    private void StoreTypedArrayType(EmittedRuntime runtime, string name, TypeBuilder type)
    {
        switch (name)
        {
            case "Int8Array": runtime.Int8ArrayType = type; break;
            case "Uint8Array": runtime.Uint8ArrayType = type; break;
            case "Uint8ClampedArray": runtime.Uint8ClampedArrayType = type; break;
            case "Int16Array": runtime.Int16ArrayType = type; break;
            case "Uint16Array": runtime.Uint16ArrayType = type; break;
            case "Int32Array": runtime.Int32ArrayType = type; break;
            case "Uint32Array": runtime.Uint32ArrayType = type; break;
            case "Float32Array": runtime.Float32ArrayType = type; break;
            case "Float64Array": runtime.Float64ArrayType = type; break;
            case "BigInt64Array": runtime.BigInt64ArrayType = type; break;
            case "BigUint64Array": runtime.BigUint64ArrayType = type; break;
        }
    }

    private void StoreTypedArrayBufferCtor(EmittedRuntime runtime, string name, ConstructorBuilder ctor)
    {
        switch (name)
        {
            case "Int8Array": runtime.Int8ArrayBufferCtor = ctor; break;
            case "Uint8Array": runtime.Uint8ArrayBufferCtor = ctor; break;
            case "Uint8ClampedArray": runtime.Uint8ClampedArrayBufferCtor = ctor; break;
            case "Int16Array": runtime.Int16ArrayBufferCtor = ctor; break;
            case "Uint16Array": runtime.Uint16ArrayBufferCtor = ctor; break;
            case "Int32Array": runtime.Int32ArrayBufferCtor = ctor; break;
            case "Uint32Array": runtime.Uint32ArrayBufferCtor = ctor; break;
            case "Float32Array": runtime.Float32ArrayBufferCtor = ctor; break;
            case "Float64Array": runtime.Float64ArrayBufferCtor = ctor; break;
            case "BigInt64Array": runtime.BigInt64ArrayBufferCtor = ctor; break;
            case "BigUint64Array": runtime.BigUint64ArrayBufferCtor = ctor; break;
        }
    }

    private void EmitBytesPerElementProperty(TypeBuilder typeBuilder, int bytesPerElement)
    {
        var prop = typeBuilder.DefineProperty("BytesPerElement", PropertyAttributes.None, _types.Int32, Type.EmptyTypes);
        var getter = typeBuilder.DefineMethod(
            "get_BytesPerElement",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            _types.Int32,
            Type.EmptyTypes
        );
        var il = getter.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4, bytesPerElement);
        il.Emit(OpCodes.Ret);
        prop.SetGetMethod(getter);
    }

    private void EmitTypeNameProperty(TypeBuilder typeBuilder, string typeName)
    {
        var prop = typeBuilder.DefineProperty("TypeName", PropertyAttributes.None, _types.String, Type.EmptyTypes);
        var getter = typeBuilder.DefineMethod(
            "get_TypeName",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            _types.String,
            Type.EmptyTypes
        );
        var il = getter.GetILGenerator();
        il.Emit(OpCodes.Ldstr, typeName);
        il.Emit(OpCodes.Ret);
        prop.SetGetMethod(getter);
    }

    private ConstructorBuilder EmitTypedArrayLengthConstructor(TypeBuilder typeBuilder, EmittedRuntime runtime, int bytesPerElement)
    {
        // Constructor: public $XArray(int length)
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [_types.Int32]
        );

        var il = ctor.GetILGenerator();

        // Create new byte array: new byte[length * bytesPerElement]
        var bufferLocal = il.DeclareLocal(typeof(byte[]));
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4, bytesPerElement);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Newarr, typeof(byte));
        il.Emit(OpCodes.Stloc, bufferLocal);

        // Call base(buffer, 0, length, null)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, bufferLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, runtime.TypedArrayBaseCtor);
        il.Emit(OpCodes.Ret);

        return ctor;
    }

    private void StoreTypedArrayLengthCtor(EmittedRuntime runtime, string name, ConstructorBuilder ctor)
    {
        switch (name)
        {
            case "Int8Array": runtime.Int8ArrayLengthCtor = ctor; break;
            case "Uint8Array": runtime.Uint8ArrayLengthCtor = ctor; break;
            case "Uint8ClampedArray": runtime.Uint8ClampedArrayLengthCtor = ctor; break;
            case "Int16Array": runtime.Int16ArrayLengthCtor = ctor; break;
            case "Uint16Array": runtime.Uint16ArrayLengthCtor = ctor; break;
            case "Int32Array": runtime.Int32ArrayLengthCtor = ctor; break;
            case "Uint32Array": runtime.Uint32ArrayLengthCtor = ctor; break;
            case "Float32Array": runtime.Float32ArrayLengthCtor = ctor; break;
            case "Float64Array": runtime.Float64ArrayLengthCtor = ctor; break;
            case "BigInt64Array": runtime.BigInt64ArrayLengthCtor = ctor; break;
            case "BigUint64Array": runtime.BigUint64ArrayLengthCtor = ctor; break;
        }
    }

    private ConstructorBuilder EmitTypedArrayBufferConstructor(TypeBuilder typeBuilder, EmittedRuntime runtime, int bytesPerElement)
    {
        // Constructor: public $XArray(object buffer, int byteOffset, int? length)
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [_types.Object, _types.Int32, typeof(int?)]
        );

        var il = ctor.GetILGenerator();

        var byteArrayLocal = il.DeclareLocal(typeof(byte[]));
        var bufByteLengthLocal = il.DeclareLocal(_types.Int32);
        var actualLengthLocal = il.DeclareLocal(_types.Int32);

        // Get byte[] from buffer
        var isSharedArrayBufferLabel = il.DefineLabel();
        var afterBufferLabel = il.DefineLabel();

        // Check if buffer is $ArrayBuffer
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.ArrayBufferType);
        il.Emit(OpCodes.Brfalse, isSharedArrayBufferLabel);

        // It's $ArrayBuffer
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.ArrayBufferType);
        il.Emit(OpCodes.Callvirt, runtime.ArrayBufferGetBuffer);
        il.Emit(OpCodes.Stloc, byteArrayLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.ArrayBufferType);
        il.Emit(OpCodes.Callvirt, runtime.ArrayBufferByteLengthGetter);
        il.Emit(OpCodes.Stloc, bufByteLengthLocal);
        il.Emit(OpCodes.Br, afterBufferLabel);

        il.MarkLabel(isSharedArrayBufferLabel);
        // Check if buffer is $SharedArrayBuffer
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.SharedArrayBufferType);
        il.Emit(OpCodes.Brfalse, afterBufferLabel);

        // It's $SharedArrayBuffer
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.SharedArrayBufferType);
        il.Emit(OpCodes.Callvirt, runtime.SharedArrayBufferGetBuffer);
        il.Emit(OpCodes.Stloc, byteArrayLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.SharedArrayBufferType);
        il.Emit(OpCodes.Callvirt, runtime.SharedArrayBufferByteLengthGetter);
        il.Emit(OpCodes.Stloc, bufByteLengthLocal);
        il.Emit(OpCodes.Br, afterBufferLabel);

        il.Emit(OpCodes.Ldstr, "TypedArray buffer constructor requires emitted ArrayBuffer/SharedArrayBuffer.");
        il.Emit(OpCodes.Newobj, _types.InvalidOperationExceptionCtorString);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(afterBufferLabel);

        // Calculate actual length
        var hasLengthLabel = il.DefineLabel();
        var afterLengthLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarga, 3);
        il.Emit(OpCodes.Call, typeof(int?).GetProperty("HasValue")!.GetGetMethod()!);
        il.Emit(OpCodes.Brfalse, hasLengthLabel);

        // Has length - use it
        il.Emit(OpCodes.Ldarga, 3);
        il.Emit(OpCodes.Call, typeof(int?).GetProperty("Value")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, actualLengthLocal);
        il.Emit(OpCodes.Br, afterLengthLabel);

        il.MarkLabel(hasLengthLabel);
        // No length - calculate from buffer: (bufByteLength - byteOffset) / bytesPerElement
        il.Emit(OpCodes.Ldloc, bufByteLengthLocal);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Ldc_I4, bytesPerElement);
        il.Emit(OpCodes.Div);
        il.Emit(OpCodes.Stloc, actualLengthLocal);

        il.MarkLabel(afterLengthLabel);

        // Call base(buffer, byteOffset, actualLength, buffer)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, byteArrayLocal);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldloc, actualLengthLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.TypedArrayBaseCtor);
        il.Emit(OpCodes.Ret);

        return ctor;
    }

    private void EmitTypedArrayIndexer(
        TypeBuilder typeBuilder,
        EmittedRuntime runtime,
        int bytesPerElement,
        bool signed,
        bool clamped,
        bool isFloat,
        bool isBigInt)
    {
        // Getter: public object Get(int index)
        var getter = typeBuilder.DefineMethod(
            "Get",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            _types.Object,
            [_types.Int32]
        );

        var getIl = getter.GetILGenerator();
        var indexLocal = getIl.DeclareLocal(_types.Int32);

        // Calculate byte index: _byteOffset + index * bytesPerElement
        getIl.Emit(OpCodes.Ldarg_0);
        getIl.Emit(OpCodes.Ldfld, _typedArrayByteOffsetField!);
        getIl.Emit(OpCodes.Ldarg_1);
        getIl.Emit(OpCodes.Ldc_I4, bytesPerElement);
        getIl.Emit(OpCodes.Mul);
        getIl.Emit(OpCodes.Add);
        getIl.Emit(OpCodes.Stloc, indexLocal);

        // Read value based on type
        if (bytesPerElement == 1)
        {
            getIl.Emit(OpCodes.Ldarg_0);
            getIl.Emit(OpCodes.Ldfld, _typedArrayBufferField!);
            getIl.Emit(OpCodes.Ldloc, indexLocal);
            getIl.Emit(OpCodes.Ldelem_U1);
            if (signed)
                getIl.Emit(OpCodes.Conv_I1);
            getIl.Emit(OpCodes.Conv_R8);
            getIl.Emit(OpCodes.Box, _types.Double);
        }
        else if (bytesPerElement == 2)
        {
            // Use BitConverter.ToInt16/ToUInt16
            getIl.Emit(OpCodes.Ldarg_0);
            getIl.Emit(OpCodes.Ldfld, _typedArrayBufferField!);
            getIl.Emit(OpCodes.Ldloc, indexLocal);
            if (signed)
                getIl.Emit(OpCodes.Call, typeof(BitConverter).GetMethod("ToInt16", [typeof(byte[]), typeof(int)])!);
            else
                getIl.Emit(OpCodes.Call, typeof(BitConverter).GetMethod("ToUInt16", [typeof(byte[]), typeof(int)])!);
            getIl.Emit(OpCodes.Conv_R8);
            getIl.Emit(OpCodes.Box, _types.Double);
        }
        else if (bytesPerElement == 4 && isFloat)
        {
            getIl.Emit(OpCodes.Ldarg_0);
            getIl.Emit(OpCodes.Ldfld, _typedArrayBufferField!);
            getIl.Emit(OpCodes.Ldloc, indexLocal);
            getIl.Emit(OpCodes.Call, typeof(BitConverter).GetMethod("ToSingle", [typeof(byte[]), typeof(int)])!);
            getIl.Emit(OpCodes.Conv_R8);
            getIl.Emit(OpCodes.Box, _types.Double);
        }
        else if (bytesPerElement == 4)
        {
            getIl.Emit(OpCodes.Ldarg_0);
            getIl.Emit(OpCodes.Ldfld, _typedArrayBufferField!);
            getIl.Emit(OpCodes.Ldloc, indexLocal);
            if (signed)
            {
                getIl.Emit(OpCodes.Call, typeof(BitConverter).GetMethod("ToInt32", [typeof(byte[]), typeof(int)])!);
                getIl.Emit(OpCodes.Conv_R8);
            }
            else
            {
                // For unsigned, zero-extend to int64 first to get correct double value
                getIl.Emit(OpCodes.Call, typeof(BitConverter).GetMethod("ToUInt32", [typeof(byte[]), typeof(int)])!);
                getIl.Emit(OpCodes.Conv_U8);  // Zero-extend uint32 to uint64
                getIl.Emit(OpCodes.Conv_R8);  // Convert to double (now correctly as 4294967295, not -1)
            }
            getIl.Emit(OpCodes.Box, _types.Double);
        }
        else if (bytesPerElement == 8 && isFloat)
        {
            getIl.Emit(OpCodes.Ldarg_0);
            getIl.Emit(OpCodes.Ldfld, _typedArrayBufferField!);
            getIl.Emit(OpCodes.Ldloc, indexLocal);
            getIl.Emit(OpCodes.Call, typeof(BitConverter).GetMethod("ToDouble", [typeof(byte[]), typeof(int)])!);
            getIl.Emit(OpCodes.Box, _types.Double);
        }
        else if (bytesPerElement == 8 && isBigInt)
        {
            // For BigInt, return as BigInteger
            getIl.Emit(OpCodes.Ldarg_0);
            getIl.Emit(OpCodes.Ldfld, _typedArrayBufferField!);
            getIl.Emit(OpCodes.Ldloc, indexLocal);
            if (signed)
                getIl.Emit(OpCodes.Call, typeof(BitConverter).GetMethod("ToInt64", [typeof(byte[]), typeof(int)])!);
            else
                getIl.Emit(OpCodes.Call, typeof(BitConverter).GetMethod("ToUInt64", [typeof(byte[]), typeof(int)])!);
            getIl.Emit(OpCodes.Newobj, typeof(System.Numerics.BigInteger).GetConstructor([signed ? typeof(long) : typeof(ulong)])!);
            getIl.Emit(OpCodes.Box, typeof(System.Numerics.BigInteger));
        }
        else
        {
            // Default - shouldn't reach here
            getIl.Emit(OpCodes.Ldnull);
        }
        getIl.Emit(OpCodes.Ret);
        typeBuilder.DefineMethodOverride(getter, runtime.TypedArrayElementGet);

        // Setter: public void Set(int index, object value)
        var setter = typeBuilder.DefineMethod(
            "Set",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            _types.Void,
            [_types.Int32, _types.Object]
        );

        var setIl = setter.GetILGenerator();
        var setIndexLocal = setIl.DeclareLocal(_types.Int32);

        // Calculate byte index
        setIl.Emit(OpCodes.Ldarg_0);
        setIl.Emit(OpCodes.Ldfld, _typedArrayByteOffsetField!);
        setIl.Emit(OpCodes.Ldarg_1);
        setIl.Emit(OpCodes.Ldc_I4, bytesPerElement);
        setIl.Emit(OpCodes.Mul);
        setIl.Emit(OpCodes.Add);
        setIl.Emit(OpCodes.Stloc, setIndexLocal);

        // Write value based on type
        if (bytesPerElement == 1)
        {
            setIl.Emit(OpCodes.Ldarg_0);
            setIl.Emit(OpCodes.Ldfld, _typedArrayBufferField!);
            setIl.Emit(OpCodes.Ldloc, setIndexLocal);
            setIl.Emit(OpCodes.Ldarg_2);
            setIl.Emit(OpCodes.Call, typeof(Convert).GetMethod("ToDouble", [typeof(object)])!);
            setIl.Emit(OpCodes.Conv_I4);
            if (clamped)
            {
                // Clamp to 0-255
                var inRangeLabel = setIl.DefineLabel();
                var endClampLabel = setIl.DefineLabel();
                var valueLocal = setIl.DeclareLocal(_types.Int32);
                setIl.Emit(OpCodes.Stloc, valueLocal);

                // Check < 0
                setIl.Emit(OpCodes.Ldloc, valueLocal);
                setIl.Emit(OpCodes.Ldc_I4_0);
                setIl.Emit(OpCodes.Bge_S, inRangeLabel);
                setIl.Emit(OpCodes.Ldc_I4_0);
                setIl.Emit(OpCodes.Br_S, endClampLabel);

                setIl.MarkLabel(inRangeLabel);
                // Check > 255
                var notOverLabel = setIl.DefineLabel();
                setIl.Emit(OpCodes.Ldloc, valueLocal);
                setIl.Emit(OpCodes.Ldc_I4, 255);
                setIl.Emit(OpCodes.Ble_S, notOverLabel);
                setIl.Emit(OpCodes.Ldc_I4, 255);
                setIl.Emit(OpCodes.Br_S, endClampLabel);

                setIl.MarkLabel(notOverLabel);
                setIl.Emit(OpCodes.Ldloc, valueLocal);

                setIl.MarkLabel(endClampLabel);
            }
            setIl.Emit(OpCodes.Conv_U1);
            setIl.Emit(OpCodes.Stelem_I1);
        }
        else if (bytesPerElement == 2)
        {
            var bytesLocal = setIl.DeclareLocal(typeof(byte[]));
            setIl.Emit(OpCodes.Ldarg_2);
            setIl.Emit(OpCodes.Call, typeof(Convert).GetMethod("ToDouble", [typeof(object)])!);
            setIl.Emit(OpCodes.Conv_I4);
            if (signed)
                setIl.Emit(OpCodes.Conv_I2);
            else
                setIl.Emit(OpCodes.Conv_U2);
            if (signed)
                setIl.Emit(OpCodes.Call, typeof(BitConverter).GetMethod("GetBytes", [typeof(short)])!);
            else
                setIl.Emit(OpCodes.Call, typeof(BitConverter).GetMethod("GetBytes", [typeof(ushort)])!);
            setIl.Emit(OpCodes.Stloc, bytesLocal);

            // Copy bytes to buffer
            setIl.Emit(OpCodes.Ldarg_0);
            setIl.Emit(OpCodes.Ldfld, _typedArrayBufferField!);
            setIl.Emit(OpCodes.Ldloc, setIndexLocal);
            setIl.Emit(OpCodes.Ldloc, bytesLocal);
            setIl.Emit(OpCodes.Ldc_I4_0);
            setIl.Emit(OpCodes.Ldelem_U1);
            setIl.Emit(OpCodes.Stelem_I1);

            setIl.Emit(OpCodes.Ldarg_0);
            setIl.Emit(OpCodes.Ldfld, _typedArrayBufferField!);
            setIl.Emit(OpCodes.Ldloc, setIndexLocal);
            setIl.Emit(OpCodes.Ldc_I4_1);
            setIl.Emit(OpCodes.Add);
            setIl.Emit(OpCodes.Ldloc, bytesLocal);
            setIl.Emit(OpCodes.Ldc_I4_1);
            setIl.Emit(OpCodes.Ldelem_U1);
            setIl.Emit(OpCodes.Stelem_I1);
        }
        else if (bytesPerElement == 4)
        {
            var bytesLocal = setIl.DeclareLocal(typeof(byte[]));
            if (isFloat)
            {
                setIl.Emit(OpCodes.Ldarg_2);
                setIl.Emit(OpCodes.Call, typeof(Convert).GetMethod("ToSingle", [typeof(object)])!);
                setIl.Emit(OpCodes.Call, typeof(BitConverter).GetMethod("GetBytes", [typeof(float)])!);
            }
            else
            {
                setIl.Emit(OpCodes.Ldarg_2);
                setIl.Emit(OpCodes.Call, typeof(Convert).GetMethod("ToDouble", [typeof(object)])!);
                if (signed)
                {
                    setIl.Emit(OpCodes.Conv_I4);
                    setIl.Emit(OpCodes.Call, typeof(BitConverter).GetMethod("GetBytes", [typeof(int)])!);
                }
                else
                {
                    setIl.Emit(OpCodes.Conv_U4);
                    setIl.Emit(OpCodes.Call, typeof(BitConverter).GetMethod("GetBytes", [typeof(uint)])!);
                }
            }
            setIl.Emit(OpCodes.Stloc, bytesLocal);

            // Use Array.Copy for 4 bytes
            setIl.Emit(OpCodes.Ldloc, bytesLocal);
            setIl.Emit(OpCodes.Ldc_I4_0);
            setIl.Emit(OpCodes.Ldarg_0);
            setIl.Emit(OpCodes.Ldfld, _typedArrayBufferField!);
            setIl.Emit(OpCodes.Ldloc, setIndexLocal);
            setIl.Emit(OpCodes.Ldc_I4_4);
            setIl.Emit(OpCodes.Call, typeof(Array).GetMethod("Copy", [typeof(Array), typeof(int), typeof(Array), typeof(int), typeof(int)])!);
        }
        else if (bytesPerElement == 8)
        {
            var bytesLocal = setIl.DeclareLocal(typeof(byte[]));
            if (isFloat)
            {
                setIl.Emit(OpCodes.Ldarg_2);
                setIl.Emit(OpCodes.Call, typeof(Convert).GetMethod("ToDouble", [typeof(object)])!);
                setIl.Emit(OpCodes.Call, typeof(BitConverter).GetMethod("GetBytes", [typeof(double)])!);
            }
            else if (isBigInt)
            {
                // For BigInt, need to convert from BigInteger to long/ulong
                setIl.Emit(OpCodes.Ldarg_2);
                setIl.Emit(OpCodes.Call, typeof(Convert).GetMethod("ToInt64", [typeof(object)])!);
                if (signed)
                    setIl.Emit(OpCodes.Call, typeof(BitConverter).GetMethod("GetBytes", [typeof(long)])!);
                else
                    setIl.Emit(OpCodes.Call, typeof(BitConverter).GetMethod("GetBytes", [typeof(ulong)])!);
            }
            else
            {
                setIl.Emit(OpCodes.Ldarg_2);
                setIl.Emit(OpCodes.Call, typeof(Convert).GetMethod("ToDouble", [typeof(object)])!);
                setIl.Emit(OpCodes.Conv_I8);
                setIl.Emit(OpCodes.Call, typeof(BitConverter).GetMethod("GetBytes", [typeof(long)])!);
            }
            setIl.Emit(OpCodes.Stloc, bytesLocal);

            // Use Array.Copy for 8 bytes
            setIl.Emit(OpCodes.Ldloc, bytesLocal);
            setIl.Emit(OpCodes.Ldc_I4_0);
            setIl.Emit(OpCodes.Ldarg_0);
            setIl.Emit(OpCodes.Ldfld, _typedArrayBufferField!);
            setIl.Emit(OpCodes.Ldloc, setIndexLocal);
            setIl.Emit(OpCodes.Ldc_I4_8);
            setIl.Emit(OpCodes.Call, typeof(Array).GetMethod("Copy", [typeof(Array), typeof(int), typeof(Array), typeof(int), typeof(int)])!);
        }

        setIl.Emit(OpCodes.Ret);
        typeBuilder.DefineMethodOverride(setter, runtime.TypedArrayElementSet);
    }
}
