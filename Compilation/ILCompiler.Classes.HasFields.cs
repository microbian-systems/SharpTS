using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class ILCompiler
{
    /// <summary>
    /// Defines $IHasFields interface method stubs for user-defined classes.
    /// Bodies are emitted later via EmitHasFieldsInterfaceMethodBodies after method definitions are available.
    /// </summary>
    private void DefineHasFieldsInterfaceMethods(TypeBuilder typeBuilder, string className, FieldInfo fieldsField)
    {
        var methodAttrs = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig;

        // get_Fields: Dictionary<string, object?> Fields { get; }
        var fieldsGetter = typeBuilder.DefineMethod(
            "get_Fields",
            methodAttrs | MethodAttributes.SpecialName,
            _types.DictionaryStringObject,
            Type.EmptyTypes
        );

        var fieldsProp = typeBuilder.DefineProperty(
            "Fields",
            PropertyAttributes.None,
            _types.DictionaryStringObject,
            null
        );
        fieldsProp.SetGetMethod(fieldsGetter);

        // GetProperty(string name) -> object?
        var getPropertyMethod = typeBuilder.DefineMethod(
            "GetProperty",
            methodAttrs,
            _types.Object,
            [_types.String]
        );

        // SetProperty(string name, object? value) -> void
        var setPropertyMethod = typeBuilder.DefineMethod(
            "SetProperty",
            methodAttrs,
            _types.Void,
            [_types.String, _types.Object]
        );

        // HasProperty(string name) -> bool
        var hasPropertyMethod = typeBuilder.DefineMethod(
            "HasProperty",
            methodAttrs,
            _types.Boolean,
            [_types.String]
        );

        // Map methods to interface slots
        typeBuilder.DefineMethodOverride(fieldsGetter, _runtime.IHasFieldsFieldsGetter);
        typeBuilder.DefineMethodOverride(getPropertyMethod, _runtime.IHasFieldsGetProperty);
        typeBuilder.DefineMethodOverride(setPropertyMethod, _runtime.IHasFieldsSetProperty);
        typeBuilder.DefineMethodOverride(hasPropertyMethod, _runtime.IHasFieldsHasProperty);

        // Store stubs for later body emission
        _classes.HasFieldsStubs[className] = new HasFieldsMethodStubs
        {
            GetFields = fieldsGetter,
            GetProperty = getPropertyMethod,
            SetProperty = setPropertyMethod,
            HasProperty = hasPropertyMethod,
            FieldsField = fieldsField
        };
    }

    /// <summary>
    /// Emits the bodies for $IHasFields interface methods.
    /// Called after DefineClassMethodsOnly so that MethodBuilders are available.
    /// Uses compile-time dispatch (no runtime reflection).
    /// </summary>
    private void EmitHasFieldsInterfaceMethodBodies(string className, Stmt.Class classStmt)
    {
        if (!_classes.HasFieldsStubs.TryGetValue(className, out var stubs))
            return;

        var fieldsField = stubs.FieldsField;

        // Emit get_Fields body: returns a dictionary combining typed backing fields + dynamic _fields
        EmitGetFieldsBody(stubs.GetFields, className, fieldsField);

        // Emit GetProperty body with compile-time dispatch
        EmitGetPropertyBody(stubs.GetProperty, className, classStmt, fieldsField);

        // Emit SetProperty body with compile-time dispatch for typed properties
        EmitSetPropertyBody(stubs.SetProperty, className, classStmt, fieldsField);

        // Emit HasProperty body with compile-time dispatch for typed backing fields
        EmitHasPropertyBody(stubs.HasProperty, className, fieldsField);
    }

    /// <summary>
    /// Emits the get_Fields body that returns a combined dictionary of typed backing fields + dynamic _fields.
    /// Typed properties stored in backing fields are not in the _fields dictionary, so we must include them.
    /// </summary>
    private void EmitGetFieldsBody(MethodBuilder method, string className, FieldInfo fieldsField)
    {
        _typedInterop.PropertyBackingFields.TryGetValue(className, out var backingFields);
        EmitGetFieldsBodyCore(method, backingFields, fieldsField);
    }

    private void EmitGetFieldsBodyCore(MethodBuilder method, Dictionary<string, FieldBuilder>? backingFields, FieldInfo fieldsField)
    {
        var il = method.GetILGenerator();

        // Check if this class has typed backing fields
        if (backingFields == null || backingFields.Count == 0)
        {
            // No typed backing fields - just return _fields directly
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, fieldsField);
            il.Emit(OpCodes.Ret);
            return;
        }

        // Create a new dictionary from _fields (copy constructor), then add backing fields
        var iDictType = typeof(IDictionary<,>).MakeGenericType(typeof(string), typeof(object));
        var copyCtor = _types.DictionaryStringObject.GetConstructor([iDictType])!;
        var setItem = _types.GetMethod(_types.DictionaryStringObject, "set_Item");

        // var result = new Dictionary<string, object?>(this._fields);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, fieldsField);
        il.Emit(OpCodes.Newobj, copyCtor);

        // result["propName"] = this._BackingField; for each typed property
        foreach (var (propName, backingField) in backingFields)
        {
            var camelName = NamingConventions.ToCamelCase(propName);
            il.Emit(OpCodes.Dup); // keep dict on stack
            il.Emit(OpCodes.Ldstr, camelName);
            il.Emit(OpCodes.Ldarg_0); // this
            il.Emit(OpCodes.Ldfld, backingField);
            if (backingField.FieldType.IsValueType)
                il.Emit(OpCodes.Box, backingField.FieldType);
            il.Emit(OpCodes.Callvirt, setItem);
        }

        // return result;
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits HasProperty body with compile-time dispatch for typed backing fields.
    /// Checks typed property names first, then falls back to _fields.ContainsKey.
    /// </summary>
    private void EmitHasPropertyBody(MethodBuilder method, string className, FieldInfo fieldsField)
    {
        _typedInterop.PropertyBackingFields.TryGetValue(className, out var backingFields);
        EmitHasPropertyBodyCore(method, backingFields, fieldsField);
    }

    private void EmitHasPropertyBodyCore(MethodBuilder method, Dictionary<string, FieldBuilder>? backingFields, FieldInfo fieldsField)
    {
        var il = method.GetILGenerator();
        var returnTrueLabel = il.DefineLabel();

        // Check typed backing field names
        if (backingFields != null)
        {
            foreach (var (propName, _) in backingFields)
            {
                var camelName = NamingConventions.ToCamelCase(propName);

                // if (name == "camelName") return true;
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldstr, camelName);
                il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
                il.Emit(OpCodes.Brtrue, returnTrueLabel);
            }
        }

        // Fall back to _fields.ContainsKey(name)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, fieldsField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("ContainsKey", [_types.String])!);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(returnTrueLabel);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits the GetProperty method body with compile-time dispatch.
    /// No runtime reflection - directly checks property/method names and calls getters or wraps methods.
    /// </summary>
    private void EmitGetPropertyBody(MethodBuilder method, string className, Stmt.Class classStmt, FieldInfo fieldsField)
    {
        var il = method.GetILGenerator();
        var valueLocal = il.DeclareLocal(_types.Object);
        var returnValueLabel = il.DefineLabel();
        var tryFieldsLabel = il.DefineLabel();
        var tryMethodsLabel = il.DefineLabel();
        var returnNullLabel = il.DefineLabel();

        // 1. Check typed properties with backing fields (compile-time dispatch)
        if (_typedInterop.PropertyBackingFields.TryGetValue(className, out var backingFields))
        {
            foreach (var (propName, backingField) in backingFields)
            {
                // Convert PascalCase property name to camelCase for JS-style access
                var camelName = NamingConventions.ToCamelCase(propName);
                var nextLabel = il.DefineLabel();

                // if (name == "camelName") return this.backingField;
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldstr, camelName);
                il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
                il.Emit(OpCodes.Brfalse, nextLabel);

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, backingField);
                // Box value types
                if (backingField.FieldType.IsValueType)
                    il.Emit(OpCodes.Box, backingField.FieldType);
                il.Emit(OpCodes.Ret);

                il.MarkLabel(nextLabel);
            }
        }

        // 2. Check _fields dictionary
        il.MarkLabel(tryFieldsLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, fieldsField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("TryGetValue", [_types.String, _types.Object.MakeByRefType()])!);
        il.Emit(OpCodes.Brfalse, tryMethodsLabel);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Ret);

        // 3. Check instance methods (compile-time dispatch)
        il.MarkLabel(tryMethodsLabel);
        if (_classes.InstanceMethods.TryGetValue(className, out var instanceMethods))
        {
            // Check if this is a generic type - generic types require runtime reflection
            // because ldtoken gives us an open generic type's method which can't be invoked
            var isGenericType = classStmt.TypeParams?.Count > 0;

            // Get the TypeBuilder for the two-token ldtoken pattern
            var typeBuilder = _classes.Builders[className];
            var getMethodFromHandleWithType = _types.GetMethod(
                _types.MethodBase, "GetMethodFromHandle",
                _types.RuntimeMethodHandle, _types.RuntimeTypeHandle);

            foreach (var (methodName, methodBuilder) in instanceMethods)
            {
                // Skip constructor
                if (methodName == "constructor")
                    continue;

                var nextLabel = il.DefineLabel();

                // if (name == "methodName") return new $TSFunction(this, methodBuilder);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldstr, methodName);
                il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
                il.Emit(OpCodes.Brfalse, nextLabel);

                // Load 'this' for target
                il.Emit(OpCodes.Ldarg_0);

                if (isGenericType)
                {
                    // For generic types, use runtime reflection to get the method from the closed type
                    // this.GetType().GetMethod("methodName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "GetType"));
                    il.Emit(OpCodes.Ldstr, methodBuilder.Name);
                    il.Emit(OpCodes.Ldc_I4, (int)(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
                    il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String, typeof(BindingFlags)));
                }
                else
                {
                    // For non-generic types, use two-token ldtoken pattern for dynamically emitted types
                    il.Emit(OpCodes.Ldtoken, methodBuilder);
                    il.Emit(OpCodes.Ldtoken, typeBuilder);
                    il.Emit(OpCodes.Call, getMethodFromHandleWithType);
                    il.Emit(OpCodes.Castclass, _types.MethodInfo);
                }
                il.Emit(OpCodes.Newobj, _runtime.TSFunctionCtor);
                il.Emit(OpCodes.Ret);

                il.MarkLabel(nextLabel);
            }
        }

        // 4. Return $Undefined.Instance if nothing found
        il.MarkLabel(returnNullLabel);
        il.Emit(OpCodes.Ldsfld, _runtime.UndefinedInstance);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits the SetProperty method body with compile-time dispatch.
    /// Handles typed properties with backing fields first, then falls back to _fields dictionary.
    /// </summary>
    private void EmitSetPropertyBody(MethodBuilder method, string className, Stmt.Class classStmt, FieldInfo fieldsField)
    {
        var il = method.GetILGenerator();
        var setFieldsLabel = il.DefineLabel();

        // 1. Check typed properties with backing fields (compile-time dispatch)
        if (_typedInterop.PropertyBackingFields.TryGetValue(className, out var backingFields))
        {
            foreach (var (propName, backingField) in backingFields)
            {
                // Convert PascalCase property name to camelCase for JS-style access
                var camelName = NamingConventions.ToCamelCase(propName);
                var nextLabel = il.DefineLabel();

                // if (name == "camelName") { this.backingField = value; return; }
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldstr, camelName);
                il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
                il.Emit(OpCodes.Brfalse, nextLabel);

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_2);
                // Unbox value types
                if (backingField.FieldType.IsValueType)
                    il.Emit(OpCodes.Unbox_Any, backingField.FieldType);
                else if (backingField.FieldType != _types.Object)
                    il.Emit(OpCodes.Castclass, backingField.FieldType);
                il.Emit(OpCodes.Stfld, backingField);
                il.Emit(OpCodes.Ret);

                il.MarkLabel(nextLabel);
            }
        }

        // 2. Fall back to _fields dictionary
        il.MarkLabel(setFieldsLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, fieldsField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [_types.String, _types.Object])!);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits the $IHasFields interface method bodies for a class expression.
    /// This overload uses _classExprs dictionaries instead of _classes dictionaries.
    /// </summary>
    private void EmitHasFieldsInterfaceMethodBodies(string className, Expr.ClassExpr classExpr)
    {
        if (!_classes.HasFieldsStubs.TryGetValue(className, out var stubs))
            return;

        var fieldsField = stubs.FieldsField;

        // Emit get_Fields body: returns a dictionary combining typed backing fields + dynamic _fields
        _classExprs.BackingFields.TryGetValue(classExpr, out var backingFields);
        EmitGetFieldsBodyCore(stubs.GetFields, backingFields, fieldsField);

        // Emit GetProperty body with compile-time dispatch
        EmitGetPropertyBodyForClassExpr(stubs.GetProperty, className, classExpr, fieldsField);

        // Emit SetProperty body with compile-time dispatch
        EmitSetPropertyBodyForClassExpr(stubs.SetProperty, className, classExpr, fieldsField);

        // Emit HasProperty body with compile-time dispatch for typed backing fields
        EmitHasPropertyBodyCore(stubs.HasProperty, backingFields, fieldsField);
    }

    /// <summary>
    /// Emits the GetProperty method body for a class expression with compile-time dispatch.
    /// Uses _classExprs.InstanceMethods for method lookup.
    /// </summary>
    private void EmitGetPropertyBodyForClassExpr(MethodBuilder method, string className, Expr.ClassExpr classExpr, FieldInfo fieldsField)
    {
        var il = method.GetILGenerator();
        var valueLocal = il.DeclareLocal(_types.Object);
        var returnValueLabel = il.DefineLabel();
        var tryFieldsLabel = il.DefineLabel();
        var tryMethodsLabel = il.DefineLabel();
        var returnNullLabel = il.DefineLabel();

        // 1. Check typed properties with backing fields (compile-time dispatch)
        if (_classExprs.BackingFields.TryGetValue(classExpr, out var backingFields))
        {
            foreach (var (propName, backingField) in backingFields)
            {
                // Convert PascalCase property name to camelCase for JS-style access
                var camelName = NamingConventions.ToCamelCase(propName);
                var nextLabel = il.DefineLabel();

                // if (name == "camelName") return this.backingField;
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldstr, camelName);
                il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
                il.Emit(OpCodes.Brfalse, nextLabel);

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, backingField);
                // Box value types
                if (backingField.FieldType.IsValueType)
                    il.Emit(OpCodes.Box, backingField.FieldType);
                il.Emit(OpCodes.Ret);

                il.MarkLabel(nextLabel);
            }
        }

        // 2. Check _fields dictionary
        il.MarkLabel(tryFieldsLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, fieldsField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("TryGetValue", [_types.String, _types.Object.MakeByRefType()])!);
        il.Emit(OpCodes.Brfalse, tryMethodsLabel);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Ret);

        // 3. Check instance methods (compile-time dispatch)
        il.MarkLabel(tryMethodsLabel);
        if (_classExprs.InstanceMethods.TryGetValue(classExpr, out var instanceMethods))
        {
            // Check if this is a generic type - generic types require runtime reflection
            // because ldtoken gives us an open generic type's method which can't be invoked
            var isGenericType = classExpr.TypeParams?.Count > 0;

            // Get the TypeBuilder for the two-token ldtoken pattern
            var typeBuilder = _classExprs.Builders[classExpr];
            var getMethodFromHandleWithType = _types.GetMethod(
                _types.MethodBase, "GetMethodFromHandle",
                _types.RuntimeMethodHandle, _types.RuntimeTypeHandle);

            foreach (var (methodName, methodBuilder) in instanceMethods)
            {
                // Skip constructor
                if (methodName == "constructor")
                    continue;

                var nextLabel = il.DefineLabel();

                // if (name == "methodName") return new $TSFunction(this, methodBuilder);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldstr, methodName);
                il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
                il.Emit(OpCodes.Brfalse, nextLabel);

                // Load 'this' for target
                il.Emit(OpCodes.Ldarg_0);

                if (isGenericType)
                {
                    // For generic types, use runtime reflection to get the method from the closed type
                    // this.GetType().GetMethod("methodName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "GetType"));
                    il.Emit(OpCodes.Ldstr, methodBuilder.Name);
                    il.Emit(OpCodes.Ldc_I4, (int)(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
                    il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String, typeof(BindingFlags)));
                }
                else
                {
                    // For non-generic types, use two-token ldtoken pattern for dynamically emitted types
                    il.Emit(OpCodes.Ldtoken, methodBuilder);
                    il.Emit(OpCodes.Ldtoken, typeBuilder);
                    il.Emit(OpCodes.Call, getMethodFromHandleWithType);
                    il.Emit(OpCodes.Castclass, _types.MethodInfo);
                }
                il.Emit(OpCodes.Newobj, _runtime.TSFunctionCtor);
                il.Emit(OpCodes.Ret);

                il.MarkLabel(nextLabel);
            }
        }

        // 4. Return $Undefined.Instance if nothing found
        il.MarkLabel(returnNullLabel);
        il.Emit(OpCodes.Ldsfld, _runtime.UndefinedInstance);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits the SetProperty method body for a class expression with compile-time dispatch.
    /// Handles typed properties with backing fields first, then falls back to _fields dictionary.
    /// </summary>
    private void EmitSetPropertyBodyForClassExpr(MethodBuilder method, string className, Expr.ClassExpr classExpr, FieldInfo fieldsField)
    {
        var il = method.GetILGenerator();
        var setFieldsLabel = il.DefineLabel();

        // 1. Check typed properties with backing fields (compile-time dispatch)
        if (_classExprs.BackingFields.TryGetValue(classExpr, out var backingFields))
        {
            foreach (var (propName, backingField) in backingFields)
            {
                // Convert PascalCase property name to camelCase for JS-style access
                var camelName = NamingConventions.ToCamelCase(propName);
                var nextLabel = il.DefineLabel();

                // if (name == "camelName") { this.backingField = value; return; }
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldstr, camelName);
                il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
                il.Emit(OpCodes.Brfalse, nextLabel);

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_2);
                // Unbox value types
                if (backingField.FieldType.IsValueType)
                    il.Emit(OpCodes.Unbox_Any, backingField.FieldType);
                else if (backingField.FieldType != _types.Object)
                    il.Emit(OpCodes.Castclass, backingField.FieldType);
                il.Emit(OpCodes.Stfld, backingField);
                il.Emit(OpCodes.Ret);

                il.MarkLabel(nextLabel);
            }
        }

        // 2. Fall back to _fields dictionary
        il.MarkLabel(setFieldsLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, fieldsField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [_types.String, _types.Object])!);
        il.Emit(OpCodes.Ret);
    }
}
