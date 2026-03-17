using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// DNS module methods for standalone assemblies.
/// Provides DNS resolution using System.Net.Dns.
/// </summary>
public partial class RuntimeEmitter
{
    private void EmitDnsModuleMethods(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        EmitDnsLookup(typeBuilder, runtime);
        EmitDnsLookupService(typeBuilder, runtime);
        EmitDnsGetLookup(typeBuilder, runtime);
        EmitDnsGetLookupService(typeBuilder, runtime);

        // Async (callback-based) DNS resolution wrappers
        EmitDnsAsyncResolveWrappers(typeBuilder, runtime);
    }

    /// <summary>
    /// Emits DnsLookup: resolves a hostname to an IP address.
    /// Signature: object DnsLookup(object hostname, object options)
    /// Returns a Dictionary with { address: string, family: number }.
    /// Options can be a number (4 or 6) to request a specific address family.
    /// </summary>
    private void EmitDnsLookup(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DnsLookup",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );
        runtime.DnsLookup = method;

        var il = method.GetILGenerator();

        // Local variables
        var hostnameLocal = il.DeclareLocal(_types.String);          // 0
        var hostEntryLocal = il.DeclareLocal(typeof(IPHostEntry));   // 1
        var resultLocal = il.DeclareLocal(_types.Object);            // 2
        var requestedFamilyLocal = il.DeclareLocal(_types.Int32);    // 3: 0 = any, 4 = IPv4, 6 = IPv6
        var selectedAddressLocal = il.DeclareLocal(typeof(IPAddress)); // 4
        var addressListLocal = il.DeclareLocal(typeof(IPAddress[])); // 5
        var indexLocal = il.DeclareLocal(_types.Int32);              // 6
        var currentAddressLocal = il.DeclareLocal(typeof(IPAddress)); // 7

        // Labels
        var parseOptionsLabel = il.DefineLabel();
        var returnLabel = il.DefineLabel();
        var checkOptionsIsDoubleLabel = il.DefineLabel();
        var optionsParsedLabel = il.DefineLabel();
        var loopStartLabel = il.DefineLabel();
        var loopBodyLabel = il.DefineLabel();
        var loopEndLabel = il.DefineLabel();
        var checkFamilyLabel = il.DefineLabel();
        var addressMatchedLabel = il.DefineLabel();
        var loopContinueLabel = il.DefineLabel();
        var foundAddressLabel = il.DefineLabel();

        // Extract hostname from arg0
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Stloc, hostnameLocal);
        il.Emit(OpCodes.Brtrue, checkOptionsIsDoubleLabel);

        // Throw error if hostname is not a string
        il.Emit(OpCodes.Ldstr, "Runtime Error: dns.lookup requires a hostname string");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);

        // Parse options - check if it's a double (family number)
        il.MarkLabel(checkOptionsIsDoubleLabel);
        il.Emit(OpCodes.Ldc_I4_0);  // default: any family
        il.Emit(OpCodes.Stloc, requestedFamilyLocal);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, parseOptionsLabel);  // null options = any family

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, typeof(double));
        il.Emit(OpCodes.Brfalse, parseOptionsLabel);  // not a double = any family

        // It's a double - get the value
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, requestedFamilyLocal);

        il.MarkLabel(parseOptionsLabel);

        // Try-catch block for Dns.GetHostEntry
        il.BeginExceptionBlock();

        // var hostEntry = Dns.GetHostEntry(hostname);
        il.Emit(OpCodes.Ldloc, hostnameLocal);
        il.Emit(OpCodes.Call, typeof(Dns).GetMethod("GetHostEntry", [typeof(string)])!);
        il.Emit(OpCodes.Stloc, hostEntryLocal);

        // var addressList = hostEntry.AddressList;
        il.Emit(OpCodes.Ldloc, hostEntryLocal);
        il.Emit(OpCodes.Callvirt, typeof(IPHostEntry).GetProperty("AddressList")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, addressListLocal);

        // selectedAddress = null
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, selectedAddressLocal);

        // index = 0
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        // Loop: for (int i = 0; i < addressList.Length; i++)
        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, addressListLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Bge, loopEndLabel);  // if index >= length, exit loop

        // currentAddress = addressList[index]
        il.Emit(OpCodes.Ldloc, addressListLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stloc, currentAddressLocal);

        // if (requestedFamily == 0) { selectedAddress = currentAddress; break; }
        il.Emit(OpCodes.Ldloc, requestedFamilyLocal);
        il.Emit(OpCodes.Brtrue, checkFamilyLabel);
        il.Emit(OpCodes.Ldloc, currentAddressLocal);
        il.Emit(OpCodes.Stloc, selectedAddressLocal);
        il.Emit(OpCodes.Br, loopEndLabel);

        // Check if address matches requested family
        il.MarkLabel(checkFamilyLabel);

        // if (requestedFamily == 4 && currentAddress.AddressFamily == InterNetwork)
        il.Emit(OpCodes.Ldloc, requestedFamilyLocal);
        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Bne_Un, loopContinueLabel);  // skip to IPv6 check

        il.Emit(OpCodes.Ldloc, currentAddressLocal);
        il.Emit(OpCodes.Callvirt, typeof(IPAddress).GetProperty("AddressFamily")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4, (int)AddressFamily.InterNetwork);
        il.Emit(OpCodes.Bne_Un, loopContinueLabel);
        il.Emit(OpCodes.Ldloc, currentAddressLocal);
        il.Emit(OpCodes.Stloc, selectedAddressLocal);
        il.Emit(OpCodes.Br, loopEndLabel);

        // if (requestedFamily == 6 && currentAddress.AddressFamily == InterNetworkV6)
        il.MarkLabel(loopContinueLabel);
        il.Emit(OpCodes.Ldloc, requestedFamilyLocal);
        il.Emit(OpCodes.Ldc_I4_6);
        il.Emit(OpCodes.Bne_Un, addressMatchedLabel);  // not 6, skip

        il.Emit(OpCodes.Ldloc, currentAddressLocal);
        il.Emit(OpCodes.Callvirt, typeof(IPAddress).GetProperty("AddressFamily")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4, (int)AddressFamily.InterNetworkV6);
        il.Emit(OpCodes.Bne_Un, addressMatchedLabel);
        il.Emit(OpCodes.Ldloc, currentAddressLocal);
        il.Emit(OpCodes.Stloc, selectedAddressLocal);
        il.Emit(OpCodes.Br, loopEndLabel);

        // index++; continue loop
        il.MarkLabel(addressMatchedLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStartLabel);

        il.MarkLabel(loopEndLabel);

        // if (selectedAddress == null) throw ENOTFOUND
        il.Emit(OpCodes.Ldloc, selectedAddressLocal);
        il.Emit(OpCodes.Brtrue, foundAddressLabel);

        il.Emit(OpCodes.Ldstr, "Runtime Error: dns.lookup ENOTFOUND ");
        il.Emit(OpCodes.Ldloc, hostnameLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "Concat", _types.String, _types.String));
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);

        il.MarkLabel(foundAddressLabel);

        // Create result object { address: string, family: number }
        var dictType = _types.DictionaryStringObject;
        var dictCtor = _types.GetDefaultConstructor(dictType);
        var addMethod = _types.GetMethod(dictType, "Add", _types.String, _types.Object);

        il.Emit(OpCodes.Newobj, dictCtor);

        // address = selectedAddress.ToString()
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldstr, "address");
        il.Emit(OpCodes.Ldloc, selectedAddressLocal);
        il.Emit(OpCodes.Callvirt, typeof(IPAddress).GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Call, addMethod);

        // family = selectedAddress.AddressFamily == InterNetwork ? 4.0 : 6.0
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldstr, "family");
        il.Emit(OpCodes.Ldloc, selectedAddressLocal);
        il.Emit(OpCodes.Callvirt, typeof(IPAddress).GetProperty("AddressFamily")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4, (int)AddressFamily.InterNetwork);
        var isIpv6Label = il.DefineLabel();
        var familyDoneLabel = il.DefineLabel();
        il.Emit(OpCodes.Bne_Un, isIpv6Label);
        il.Emit(OpCodes.Ldc_R8, 4.0);
        il.Emit(OpCodes.Br, familyDoneLabel);
        il.MarkLabel(isIpv6Label);
        il.Emit(OpCodes.Ldc_R8, 6.0);
        il.MarkLabel(familyDoneLabel);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Call, addMethod);

        // Wrap in TSObject and store in result
        il.Emit(OpCodes.Call, runtime.CreateObject);
        il.Emit(OpCodes.Stloc, resultLocal);

        // Leave try block
        il.Emit(OpCodes.Leave, returnLabel);

        // Catch Exception - rethrow as DNS error
        il.BeginCatchBlock(_types.Exception);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "Runtime Error: dns.lookup ENOTFOUND ");
        il.Emit(OpCodes.Ldloc, hostnameLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "Concat", _types.String, _types.String));
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);

        il.EndExceptionBlock();

        il.MarkLabel(returnLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits DnsLookupService: resolves address and port to hostname and service.
    /// Signature: object DnsLookupService(object address, object port)
    /// Returns a Dictionary with { hostname: string, service: string }
    /// </summary>
    private void EmitDnsLookupService(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DnsLookupService",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );
        runtime.DnsLookupService = method;

        var il = method.GetILGenerator();

        // Local variables
        var addressStrLocal = il.DeclareLocal(_types.String);
        var portLocal = il.DeclareLocal(_types.Int32);
        var ipAddressLocal = il.DeclareLocal(typeof(IPAddress));
        var hostEntryLocal = il.DeclareLocal(typeof(IPHostEntry));
        var resultLocal = il.DeclareLocal(_types.Object);

        // Labels
        var parsePortLabel = il.DefineLabel();
        var lookupLabel = il.DefineLabel();
        var returnLabel = il.DefineLabel();
        var parseOkLabel = il.DefineLabel();

        // Extract address string from arg0
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Stloc, addressStrLocal);
        il.Emit(OpCodes.Brtrue, parsePortLabel);

        // Throw if not string
        il.Emit(OpCodes.Ldstr, "Runtime Error: dns.lookupService address must be a string");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);

        // Extract port from arg1
        il.MarkLabel(parsePortLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, typeof(double));
        il.Emit(OpCodes.Brfalse, lookupLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, portLocal);

        il.MarkLabel(lookupLabel);

        // Try-catch block
        il.BeginExceptionBlock();

        // Parse IP address
        il.Emit(OpCodes.Ldloc, addressStrLocal);
        il.Emit(OpCodes.Ldloca, ipAddressLocal);
        il.Emit(OpCodes.Call, typeof(IPAddress).GetMethod("TryParse", [typeof(string), typeof(IPAddress).MakeByRefType()])!);
        il.Emit(OpCodes.Brtrue, parseOkLabel);

        // Throw invalid address
        il.Emit(OpCodes.Ldstr, "Runtime Error: dns.lookupService invalid address ");
        il.Emit(OpCodes.Ldloc, addressStrLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "Concat", _types.String, _types.String));
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);

        il.MarkLabel(parseOkLabel);

        // Reverse DNS lookup
        il.Emit(OpCodes.Ldloc, ipAddressLocal);
        il.Emit(OpCodes.Call, typeof(Dns).GetMethod("GetHostEntry", [typeof(IPAddress)])!);
        il.Emit(OpCodes.Stloc, hostEntryLocal);

        // Create result object { hostname: string, service: string }
        var dictType = _types.DictionaryStringObject;
        var dictCtor = _types.GetDefaultConstructor(dictType);
        var addMethod = _types.GetMethod(dictType, "Add", _types.String, _types.Object);

        il.Emit(OpCodes.Newobj, dictCtor);

        // hostname
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldstr, "hostname");
        il.Emit(OpCodes.Ldloc, hostEntryLocal);
        il.Emit(OpCodes.Callvirt, typeof(IPHostEntry).GetProperty("HostName")!.GetGetMethod()!);
        il.Emit(OpCodes.Call, addMethod);

        // service (just port number as string)
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldstr, "service");
        il.Emit(OpCodes.Ldloca, portLocal);
        il.Emit(OpCodes.Call, _types.GetMethodNoParams(_types.Int32, "ToString"));
        il.Emit(OpCodes.Call, addMethod);

        // Wrap in TSObject and store in result
        il.Emit(OpCodes.Call, runtime.CreateObject);
        il.Emit(OpCodes.Stloc, resultLocal);

        // Leave try block
        il.Emit(OpCodes.Leave, returnLabel);

        // Catch Exception
        il.BeginCatchBlock(_types.Exception);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "Runtime Error: dns.lookupService ENOTFOUND ");
        il.Emit(OpCodes.Ldloc, addressStrLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "Concat", _types.String, _types.String));
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);

        il.EndExceptionBlock();

        il.MarkLabel(returnLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits DnsGetLookup: returns a TSFunction wrapper for dns.lookup.
    /// Creates both the implementation method and the getter.
    /// </summary>
    private void EmitDnsGetLookup(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // First, create the implementation method that takes List<object> args
        var implMethod = typeBuilder.DefineMethod(
            "DnsLookupImpl",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ListOfObject]
        );

        var il = implMethod.GetILGenerator();

        // Extract args[0] (hostname) and args[1] (options) from the list
        var hostnameLocal = il.DeclareLocal(_types.Object);
        var optionsLocal = il.DeclareLocal(_types.Object);

        // hostname = args.Count > 0 ? args[0] : null
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetPropertyGetter(_types.ListOfObject, "Count"));
        il.Emit(OpCodes.Ldc_I4_0);
        var skipHostnameLabel = il.DefineLabel();
        var afterHostnameLabel = il.DefineLabel();
        il.Emit(OpCodes.Ble, skipHostnameLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "get_Item", _types.Int32));
        il.Emit(OpCodes.Br, afterHostnameLabel);
        il.MarkLabel(skipHostnameLabel);
        il.Emit(OpCodes.Ldnull);
        il.MarkLabel(afterHostnameLabel);
        il.Emit(OpCodes.Stloc, hostnameLocal);

        // options = args.Count > 1 ? args[1] : null
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetPropertyGetter(_types.ListOfObject, "Count"));
        il.Emit(OpCodes.Ldc_I4_1);
        var skipOptionsLabel = il.DefineLabel();
        var afterOptionsLabel = il.DefineLabel();
        il.Emit(OpCodes.Ble, skipOptionsLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "get_Item", _types.Int32));
        il.Emit(OpCodes.Br, afterOptionsLabel);
        il.MarkLabel(skipOptionsLabel);
        il.Emit(OpCodes.Ldnull);
        il.MarkLabel(afterOptionsLabel);
        il.Emit(OpCodes.Stloc, optionsLocal);

        // Call DnsLookup(hostname, options)
        il.Emit(OpCodes.Ldloc, hostnameLocal);
        il.Emit(OpCodes.Ldloc, optionsLocal);
        il.Emit(OpCodes.Call, runtime.DnsLookup);
        il.Emit(OpCodes.Ret);

        // Now create the getter method that returns a TSFunction
        var getterMethod = typeBuilder.DefineMethod(
            "DnsGetLookup",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            Type.EmptyTypes
        );
        runtime.DnsGetLookup = getterMethod;

        var getterIl = getterMethod.GetILGenerator();

        // return new TSFunction(null, implMethod)
        getterIl.Emit(OpCodes.Ldnull); // target (static method)
        getterIl.Emit(OpCodes.Ldtoken, implMethod);
        getterIl.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle", _types.RuntimeMethodHandle));
        getterIl.Emit(OpCodes.Castclass, _types.MethodInfo);
        getterIl.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        getterIl.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits DnsGetLookupService: returns a TSFunction wrapper for dns.lookupService.
    /// Creates both the implementation method and the getter.
    /// </summary>
    private void EmitDnsGetLookupService(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // First, create the implementation method that takes List<object> args
        var implMethod = typeBuilder.DefineMethod(
            "DnsLookupServiceImpl",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ListOfObject]
        );

        var il = implMethod.GetILGenerator();

        // Extract args[0] (address) and args[1] (port) from the list
        var addressLocal = il.DeclareLocal(_types.Object);
        var portLocal = il.DeclareLocal(_types.Object);

        // address = args.Count > 0 ? args[0] : null
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetPropertyGetter(_types.ListOfObject, "Count"));
        il.Emit(OpCodes.Ldc_I4_0);
        var skipAddressLabel = il.DefineLabel();
        var afterAddressLabel = il.DefineLabel();
        il.Emit(OpCodes.Ble, skipAddressLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "get_Item", _types.Int32));
        il.Emit(OpCodes.Br, afterAddressLabel);
        il.MarkLabel(skipAddressLabel);
        il.Emit(OpCodes.Ldnull);
        il.MarkLabel(afterAddressLabel);
        il.Emit(OpCodes.Stloc, addressLocal);

        // port = args.Count > 1 ? args[1] : null
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetPropertyGetter(_types.ListOfObject, "Count"));
        il.Emit(OpCodes.Ldc_I4_1);
        var skipPortLabel = il.DefineLabel();
        var afterPortLabel = il.DefineLabel();
        il.Emit(OpCodes.Ble, skipPortLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "get_Item", _types.Int32));
        il.Emit(OpCodes.Br, afterPortLabel);
        il.MarkLabel(skipPortLabel);
        il.Emit(OpCodes.Ldnull);
        il.MarkLabel(afterPortLabel);
        il.Emit(OpCodes.Stloc, portLocal);

        // Call DnsLookupService(address, port)
        il.Emit(OpCodes.Ldloc, addressLocal);
        il.Emit(OpCodes.Ldloc, portLocal);
        il.Emit(OpCodes.Call, runtime.DnsLookupService);
        il.Emit(OpCodes.Ret);

        // Now create the getter method that returns a TSFunction
        var getterMethod = typeBuilder.DefineMethod(
            "DnsGetLookupService",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            Type.EmptyTypes
        );
        runtime.DnsGetLookupService = getterMethod;

        var getterIl = getterMethod.GetILGenerator();

        // return new TSFunction(null, implMethod)
        getterIl.Emit(OpCodes.Ldnull); // target (static method)
        getterIl.Emit(OpCodes.Ldtoken, implMethod);
        getterIl.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle", _types.RuntimeMethodHandle));
        getterIl.Emit(OpCodes.Castclass, _types.MethodInfo);
        getterIl.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        getterIl.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits async DNS resolution wrapper methods: resolve, resolve4, resolve6, reverse.
    /// Each calls the sync DnsLookup method and invokes the callback with (null, result) or (error, null).
    /// </summary>
    private void EmitDnsAsyncResolveWrappers(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // resolve4(hostname, callback) -> calls DnsLookup with family=4, returns array of IPs
        EmitDnsResolveByFamily(typeBuilder, runtime, "resolve4", 4);

        // resolve6(hostname, callback) -> calls DnsLookup with family=6, returns array of IPs
        EmitDnsResolveByFamily(typeBuilder, runtime, "resolve6", 6);

        // resolve(hostname, rrtype_or_callback, callback?) -> calls DnsLookup
        EmitDnsResolveWrapper(typeBuilder, runtime);

        // reverse(ip, callback) -> reverse DNS lookup
        EmitDnsReverseWrapper(typeBuilder, runtime);
    }

    /// <summary>
    /// Emits resolve4/resolve6: hostname + callback -> calls DnsLookup with fixed family, extracts addresses array.
    /// </summary>
    private void EmitDnsResolveByFamily(TypeBuilder typeBuilder, EmittedRuntime runtime, string methodName, int family)
    {
        // DnsWrapper_resolve4(hostname, callback)
        var method = typeBuilder.DefineMethod(
            "DnsWrapper_" + methodName,
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]);

        var il = method.GetILGenerator();
        var resultLocal = il.DeclareLocal(_types.Object);
        var endLabel = il.DefineLabel();

        il.BeginExceptionBlock();

        // Call DnsLookup(hostname, family_number) - returns { address, family } but we need array of strings
        // Use System.Net.Dns.GetHostEntry directly for array result
        // hostname.ToString()
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));

        // Call Dns.GetHostEntry(hostname) -> IPHostEntry
        il.Emit(OpCodes.Call, typeof(Dns).GetMethod("GetHostEntry", [typeof(string)])!);
        var hostEntryLocal = il.DeclareLocal(typeof(IPHostEntry));
        il.Emit(OpCodes.Stloc, hostEntryLocal);

        // Get AddressList
        il.Emit(OpCodes.Ldloc, hostEntryLocal);
        il.Emit(OpCodes.Callvirt, typeof(IPHostEntry).GetProperty("AddressList")!.GetGetMethod()!);
        var addressListLocal = il.DeclareLocal(typeof(IPAddress[]));
        il.Emit(OpCodes.Stloc, addressListLocal);

        // Build List<object> of matching addresses
        il.Emit(OpCodes.Newobj, _types.ListOfObject.GetConstructor(Type.EmptyTypes)!);
        var listLocal = il.DeclareLocal(_types.ListOfObject);
        il.Emit(OpCodes.Stloc, listLocal);

        // for (int i = 0; i < addressList.Length; i++)
        var indexLocal = il.DeclareLocal(typeof(int));
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);
        var loopStart = il.DefineLabel();
        var loopCond = il.DefineLabel();
        il.Emit(OpCodes.Br, loopCond);

        il.MarkLabel(loopStart);
        // if (addressList[i].AddressFamily == target)
        il.Emit(OpCodes.Ldloc, addressListLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Callvirt, typeof(IPAddress).GetProperty("AddressFamily")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4, family == 4 ? (int)AddressFamily.InterNetwork : (int)AddressFamily.InterNetworkV6);
        var skipLabel = il.DefineLabel();
        il.Emit(OpCodes.Bne_Un, skipLabel);

        // list.Add(address.ToString())
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Ldloc, addressListLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(typeof(IPAddress), "ToString"));
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add")!);

        il.MarkLabel(skipLabel);
        // i++
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);

        // i < addressList.Length
        il.MarkLabel(loopCond);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, addressListLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Blt, loopStart);

        // Create $Array from list
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Newobj, runtime.TSArrayCtor);
        il.Emit(OpCodes.Stloc, resultLocal);

        // callback.Invoke([null, result])
        il.Emit(OpCodes.Ldarg_1); // callback
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvoke);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Leave, endLabel);

        // catch
        il.BeginCatchBlock(typeof(Exception));
        var exLocal = il.DeclareLocal(typeof(Exception));
        il.Emit(OpCodes.Stloc, exLocal);

        il.Emit(OpCodes.Ldarg_1); // callback
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, exLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(typeof(Exception), "get_Message"));
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvoke);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Leave, endLabel);

        il.EndExceptionBlock();

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        runtime.RegisterBuiltInModuleMethod("dns", methodName, method);
    }

    /// <summary>
    /// Emits resolve(hostname, rrtype_or_callback, callback?):
    /// If 2 args: resolve(hostname, callback) with default rrtype="A"
    /// If 3 args: resolve(hostname, rrtype, callback)
    /// </summary>
    private void EmitDnsResolveWrapper(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DnsWrapper_resolve",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object, _types.Object]);

        var il = method.GetILGenerator();
        var callbackLocal = il.DeclareLocal(_types.Object);
        var endLabel = il.DefineLabel();

        // If arg2 != null, callback=arg2 (3-arg form)
        // If arg2 == null, callback=arg1 (2-arg form, default rrtype)
        var threeArgLabel = il.DefineLabel();
        var afterCallbackLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Brtrue, threeArgLabel);

        // 2-arg form: callback=arg1
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stloc, callbackLocal);
        il.Emit(OpCodes.Br, afterCallbackLabel);

        il.MarkLabel(threeArgLabel);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Stloc, callbackLocal);

        il.MarkLabel(afterCallbackLabel);

        il.BeginExceptionBlock();

        // Use DnsLookup(hostname, null) which returns {address, family}
        // For resolve() we want array of strings, so use GetHostEntry
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Call, typeof(Dns).GetMethod("GetHostEntry", [typeof(string)])!);
        var hostEntryLocal = il.DeclareLocal(typeof(IPHostEntry));
        il.Emit(OpCodes.Stloc, hostEntryLocal);

        // Build list from all addresses
        il.Emit(OpCodes.Newobj, _types.ListOfObject.GetConstructor(Type.EmptyTypes)!);
        var listLocal = il.DeclareLocal(_types.ListOfObject);
        il.Emit(OpCodes.Stloc, listLocal);

        il.Emit(OpCodes.Ldloc, hostEntryLocal);
        il.Emit(OpCodes.Callvirt, typeof(IPHostEntry).GetProperty("AddressList")!.GetGetMethod()!);
        var addrArrayLocal = il.DeclareLocal(typeof(IPAddress[]));
        il.Emit(OpCodes.Stloc, addrArrayLocal);

        var idxLocal = il.DeclareLocal(typeof(int));
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, idxLocal);
        var loopS = il.DefineLabel();
        var loopC = il.DefineLabel();
        il.Emit(OpCodes.Br, loopC);
        il.MarkLabel(loopS);

        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Ldloc, addrArrayLocal);
        il.Emit(OpCodes.Ldloc, idxLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(typeof(IPAddress), "ToString"));
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add")!);

        il.Emit(OpCodes.Ldloc, idxLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, idxLocal);
        il.MarkLabel(loopC);
        il.Emit(OpCodes.Ldloc, idxLocal);
        il.Emit(OpCodes.Ldloc, addrArrayLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Blt, loopS);

        // Create $Array
        var resultLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Newobj, runtime.TSArrayCtor);
        il.Emit(OpCodes.Stloc, resultLocal);

        // callback(null, result)
        il.Emit(OpCodes.Ldloc, callbackLocal);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvoke);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Leave, endLabel);

        il.BeginCatchBlock(typeof(Exception));
        var exLocal = il.DeclareLocal(typeof(Exception));
        il.Emit(OpCodes.Stloc, exLocal);

        il.Emit(OpCodes.Ldloc, callbackLocal);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, exLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(typeof(Exception), "get_Message"));
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvoke);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Leave, endLabel);

        il.EndExceptionBlock();

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        runtime.RegisterBuiltInModuleMethod("dns", "resolve", method);
    }

    /// <summary>
    /// Emits reverse(ip, callback): reverse DNS lookup.
    /// </summary>
    private void EmitDnsReverseWrapper(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DnsWrapper_reverse",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]);

        var il = method.GetILGenerator();
        var resultLocal = il.DeclareLocal(_types.Object);
        var endLabel = il.DefineLabel();

        il.BeginExceptionBlock();

        // Parse IP address
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Call, typeof(IPAddress).GetMethod("Parse", [typeof(string)])!);
        var ipLocal = il.DeclareLocal(typeof(IPAddress));
        il.Emit(OpCodes.Stloc, ipLocal);

        // Dns.GetHostEntry(ip) -> IPHostEntry
        il.Emit(OpCodes.Ldloc, ipLocal);
        il.Emit(OpCodes.Call, typeof(Dns).GetMethod("GetHostEntry", [typeof(IPAddress)])!);
        var hostEntryLocal = il.DeclareLocal(typeof(IPHostEntry));
        il.Emit(OpCodes.Stloc, hostEntryLocal);

        // Build array with hostname
        il.Emit(OpCodes.Newobj, _types.ListOfObject.GetConstructor(Type.EmptyTypes)!);
        var listLocal = il.DeclareLocal(_types.ListOfObject);
        il.Emit(OpCodes.Stloc, listLocal);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Ldloc, hostEntryLocal);
        il.Emit(OpCodes.Callvirt, typeof(IPHostEntry).GetProperty("HostName")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add")!);

        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Newobj, runtime.TSArrayCtor);
        il.Emit(OpCodes.Stloc, resultLocal);

        // callback(null, result)
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvoke);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Leave, endLabel);

        il.BeginCatchBlock(typeof(Exception));
        var exLocal = il.DeclareLocal(typeof(Exception));
        il.Emit(OpCodes.Stloc, exLocal);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, exLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(typeof(Exception), "get_Message"));
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvoke);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Leave, endLabel);

        il.EndExceptionBlock();

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        runtime.RegisterBuiltInModuleMethod("dns", "reverse", method);
    }
}
