using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;
using DnsClient;
using DnsClient.Protocol;

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

        // DNS record resolution (MX, TXT, SRV, CNAME, NS, SOA, PTR, CAA, NAPTR)
        EmitDnsConvertList(typeBuilder, runtime);
        EmitDnsDoQuery(typeBuilder, runtime);
        EmitDnsResolveRecord(typeBuilder, runtime);

        // Async (callback-based) DNS resolution wrappers
        EmitDnsAsyncResolveWrappers(typeBuilder, runtime);

        // Async wrappers for DNS record types
        EmitDnsRecordTypeWrappers(typeBuilder, runtime);
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
    /// Emits DnsResolveRecord: resolves DNS records by type (MX, TXT, SRV, etc.).
    /// Uses late-binding via Type.GetType to call DnsRecordResolver.Resolve at runtime.
    /// This avoids embedding a SharpTS assembly reference in the compiled DLL.
    /// Signature: object DnsResolveRecord(object hostname, object rrtype)
    /// </summary>
    private void EmitDnsResolveRecord(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DnsResolveRecord",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]);
        runtime.DnsResolveRecord = method;

        var il = method.GetILGenerator();

        var hostnameLocal = il.DeclareLocal(_types.String);   // 0
        var rrtypeLocal = il.DeclareLocal(_types.String);     // 1
        var resultLocal = il.DeclareLocal(_types.Object);     // 2
        var returnLabel = il.DefineLabel();

        // hostname = arg0.ToString()
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Stloc, hostnameLocal);

        // rrtype = arg1.ToString().ToUpperInvariant()
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.String, "ToUpperInvariant"));
        il.Emit(OpCodes.Stloc, rrtypeLocal);

        // Rrtype switch: series of string comparisons
        var mxLabel = il.DefineLabel();
        var txtLabel = il.DefineLabel();
        var srvLabel = il.DefineLabel();
        var cnameLabel = il.DefineLabel();
        var nsLabel = il.DefineLabel();
        var soaLabel = il.DefineLabel();
        var ptrLabel = il.DefineLabel();
        var caaLabel = il.DefineLabel();
        var naptrLabel = il.DefineLabel();
        var aLabel = il.DefineLabel();
        var aaaaLabel = il.DefineLabel();
        var unknownLabel = il.DefineLabel();

        var strEquals = typeof(string).GetMethod("Equals", [typeof(string)])!;

        EmitRrtypeCheck(il, rrtypeLocal, "MX", mxLabel, strEquals);
        EmitRrtypeCheck(il, rrtypeLocal, "TXT", txtLabel, strEquals);
        EmitRrtypeCheck(il, rrtypeLocal, "SRV", srvLabel, strEquals);
        EmitRrtypeCheck(il, rrtypeLocal, "CNAME", cnameLabel, strEquals);
        EmitRrtypeCheck(il, rrtypeLocal, "NS", nsLabel, strEquals);
        EmitRrtypeCheck(il, rrtypeLocal, "SOA", soaLabel, strEquals);
        EmitRrtypeCheck(il, rrtypeLocal, "PTR", ptrLabel, strEquals);
        EmitRrtypeCheck(il, rrtypeLocal, "CAA", caaLabel, strEquals);
        EmitRrtypeCheck(il, rrtypeLocal, "NAPTR", naptrLabel, strEquals);
        EmitRrtypeCheck(il, rrtypeLocal, "A", aLabel, strEquals);
        EmitRrtypeCheck(il, rrtypeLocal, "AAAA", aaaaLabel, strEquals);
        il.Emit(OpCodes.Br, unknownLabel);

        // === MX section ===
        il.MarkLabel(mxLabel);
        EmitDnsQueryCall(il, runtime, hostnameLocal, (int)QueryType.MX);
        var mxResponseLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, mxResponseLocal);
        var mxList = EmitDnsAnswerIteration(il, runtime, typeof(MxRecord), mxResponseLocal, hostnameLocal, "resolveMx",
            (emitIl, recordLocal, listLocal) =>
            {
                // Create dictionary { exchange: string, priority: number }
                emitIl.Emit(OpCodes.Newobj, _types.DictionaryStringObject.GetConstructor(Type.EmptyTypes)!);
                var dictLocal = emitIl.DeclareLocal(_types.DictionaryStringObject);
                emitIl.Emit(OpCodes.Stloc, dictLocal);

                // dict["exchange"] = record.Exchange.Value.TrimEnd('.')
                emitIl.Emit(OpCodes.Ldloc, dictLocal);
                emitIl.Emit(OpCodes.Ldstr, "exchange");
                emitIl.Emit(OpCodes.Ldloc, recordLocal);
                EmitDnsStringProperty(emitIl, typeof(MxRecord), "Exchange");
                emitIl.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item")!);

                // dict["priority"] = (double)record.Preference
                emitIl.Emit(OpCodes.Ldloc, dictLocal);
                emitIl.Emit(OpCodes.Ldstr, "priority");
                emitIl.Emit(OpCodes.Ldloc, recordLocal);
                emitIl.Emit(OpCodes.Callvirt, typeof(MxRecord).GetProperty("Preference")!.GetGetMethod()!);
                emitIl.Emit(OpCodes.Conv_R8);
                emitIl.Emit(OpCodes.Box, typeof(double));
                emitIl.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item")!);

                // list.Add(dict)
                emitIl.Emit(OpCodes.Ldloc, listLocal);
                emitIl.Emit(OpCodes.Ldloc, dictLocal);
                emitIl.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add")!);
            });
        // Wrap list -> $Array via DnsConvertList (handles inner dicts -> $Object)
        il.Emit(OpCodes.Ldloc, mxList);
        il.Emit(OpCodes.Call, runtime.DnsConvertList);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Br, returnLabel);

        // === TXT section ===
        il.MarkLabel(txtLabel);
        EmitDnsQueryCall(il, runtime, hostnameLocal, (int)QueryType.TXT);
        var txtResponseLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, txtResponseLocal);
        var txtList = EmitDnsAnswerIteration(il, runtime, typeof(TxtRecord), txtResponseLocal, hostnameLocal, "resolveTxt",
            (emitIl, recordLocal, listLocal) =>
            {
                // Each TXT record has Text property (IReadOnlyList<string>)
                // Convert to List<object?> of strings, then wrap as inner list
                emitIl.Emit(OpCodes.Newobj, _types.ListOfObject.GetConstructor(Type.EmptyTypes)!);
                var innerList = emitIl.DeclareLocal(_types.ListOfObject);
                emitIl.Emit(OpCodes.Stloc, innerList);

                // Get Text property
                emitIl.Emit(OpCodes.Ldloc, recordLocal);
                emitIl.Emit(OpCodes.Callvirt, typeof(TxtRecord).GetProperty("Text")!.GetGetMethod()!);
                // IReadOnlyList<string> -> iterate
                var txtEnumerator = emitIl.DeclareLocal(typeof(IEnumerator<string>));
                emitIl.Emit(OpCodes.Callvirt, typeof(IEnumerable<string>).GetMethod("GetEnumerator")!);
                emitIl.Emit(OpCodes.Stloc, txtEnumerator);
                var txtLoopStart = emitIl.DefineLabel();
                var txtLoopEnd = emitIl.DefineLabel();
                emitIl.MarkLabel(txtLoopStart);
                emitIl.Emit(OpCodes.Ldloc, txtEnumerator);
                emitIl.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!);
                emitIl.Emit(OpCodes.Brfalse, txtLoopEnd);
                emitIl.Emit(OpCodes.Ldloc, innerList);
                emitIl.Emit(OpCodes.Ldloc, txtEnumerator);
                emitIl.Emit(OpCodes.Callvirt, typeof(IEnumerator<string>).GetProperty("Current")!.GetGetMethod()!);
                emitIl.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add")!);
                emitIl.Emit(OpCodes.Br, txtLoopStart);
                emitIl.MarkLabel(txtLoopEnd);

                // list.Add(innerList) — will be converted to $Array by DnsConvertList
                emitIl.Emit(OpCodes.Ldloc, listLocal);
                emitIl.Emit(OpCodes.Ldloc, innerList);
                emitIl.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add")!);
            });
        il.Emit(OpCodes.Ldloc, txtList);
        il.Emit(OpCodes.Call, runtime.DnsConvertList);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Br, returnLabel);

        // === SRV section ===
        il.MarkLabel(srvLabel);
        EmitDnsQueryCall(il, runtime, hostnameLocal, (int)QueryType.SRV);
        var srvResponseLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, srvResponseLocal);
        var srvList = EmitDnsAnswerIteration(il, runtime, typeof(SrvRecord), srvResponseLocal, hostnameLocal, "resolveSrv",
            (emitIl, recordLocal, listLocal) =>
            {
                emitIl.Emit(OpCodes.Newobj, _types.DictionaryStringObject.GetConstructor(Type.EmptyTypes)!);
                var dictLocal = emitIl.DeclareLocal(_types.DictionaryStringObject);
                emitIl.Emit(OpCodes.Stloc, dictLocal);

                // name = Target.Value.TrimEnd('.')
                emitIl.Emit(OpCodes.Ldloc, dictLocal);
                emitIl.Emit(OpCodes.Ldstr, "name");
                emitIl.Emit(OpCodes.Ldloc, recordLocal);
                EmitDnsStringProperty(emitIl, typeof(SrvRecord), "Target");
                emitIl.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item")!);

                // port = (double)Port
                emitIl.Emit(OpCodes.Ldloc, dictLocal);
                emitIl.Emit(OpCodes.Ldstr, "port");
                emitIl.Emit(OpCodes.Ldloc, recordLocal);
                emitIl.Emit(OpCodes.Callvirt, typeof(SrvRecord).GetProperty("Port")!.GetGetMethod()!);
                emitIl.Emit(OpCodes.Conv_R8);
                emitIl.Emit(OpCodes.Box, typeof(double));
                emitIl.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item")!);

                // priority = (double)Priority
                emitIl.Emit(OpCodes.Ldloc, dictLocal);
                emitIl.Emit(OpCodes.Ldstr, "priority");
                emitIl.Emit(OpCodes.Ldloc, recordLocal);
                emitIl.Emit(OpCodes.Callvirt, typeof(SrvRecord).GetProperty("Priority")!.GetGetMethod()!);
                emitIl.Emit(OpCodes.Conv_R8);
                emitIl.Emit(OpCodes.Box, typeof(double));
                emitIl.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item")!);

                // weight = (double)Weight
                emitIl.Emit(OpCodes.Ldloc, dictLocal);
                emitIl.Emit(OpCodes.Ldstr, "weight");
                emitIl.Emit(OpCodes.Ldloc, recordLocal);
                emitIl.Emit(OpCodes.Callvirt, typeof(SrvRecord).GetProperty("Weight")!.GetGetMethod()!);
                emitIl.Emit(OpCodes.Conv_R8);
                emitIl.Emit(OpCodes.Box, typeof(double));
                emitIl.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item")!);

                emitIl.Emit(OpCodes.Ldloc, listLocal);
                emitIl.Emit(OpCodes.Ldloc, dictLocal);
                emitIl.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add")!);
            });
        il.Emit(OpCodes.Ldloc, srvList);
        il.Emit(OpCodes.Call, runtime.DnsConvertList);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Br, returnLabel);

        // === CNAME section ===
        il.MarkLabel(cnameLabel);
        EmitDnsQueryCall(il, runtime, hostnameLocal, (int)QueryType.CNAME);
        var cnameResponseLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, cnameResponseLocal);
        var cnameList = EmitDnsStringRecordIteration(il, runtime, typeof(CNameRecord), "CanonicalName",
            cnameResponseLocal, hostnameLocal, "resolveCname");
        il.Emit(OpCodes.Ldloc, cnameList);
        il.Emit(OpCodes.Newobj, runtime.TSArrayCtor);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Br, returnLabel);

        // === NS section ===
        il.MarkLabel(nsLabel);
        EmitDnsQueryCall(il, runtime, hostnameLocal, (int)QueryType.NS);
        var nsResponseLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, nsResponseLocal);
        var nsList = EmitDnsStringRecordIteration(il, runtime, typeof(NsRecord), "NSDName",
            nsResponseLocal, hostnameLocal, "resolveNs");
        il.Emit(OpCodes.Ldloc, nsList);
        il.Emit(OpCodes.Newobj, runtime.TSArrayCtor);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Br, returnLabel);

        // === SOA section ===
        il.MarkLabel(soaLabel);
        EmitDnsQueryCall(il, runtime, hostnameLocal, (int)QueryType.SOA);
        var soaResponseLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, soaResponseLocal);
        EmitDnsSoaProcessing(il, runtime, soaResponseLocal, hostnameLocal, resultLocal);
        il.Emit(OpCodes.Br, returnLabel);

        // === PTR section ===
        il.MarkLabel(ptrLabel);
        EmitDnsQueryCall(il, runtime, hostnameLocal, (int)QueryType.PTR);
        var ptrResponseLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, ptrResponseLocal);
        var ptrList = EmitDnsStringRecordIteration(il, runtime, typeof(PtrRecord), "PtrDomainName",
            ptrResponseLocal, hostnameLocal, "resolvePtr");
        il.Emit(OpCodes.Ldloc, ptrList);
        il.Emit(OpCodes.Newobj, runtime.TSArrayCtor);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Br, returnLabel);

        // === CAA section ===
        il.MarkLabel(caaLabel);
        EmitDnsQueryCall(il, runtime, hostnameLocal, (int)QueryType.CAA);
        var caaResponseLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, caaResponseLocal);
        var caaList = EmitDnsAnswerIteration(il, runtime, typeof(CaaRecord), caaResponseLocal, hostnameLocal, "resolveCaa",
            (emitIl, recordLocal, listLocal) =>
            {
                emitIl.Emit(OpCodes.Newobj, _types.DictionaryStringObject.GetConstructor(Type.EmptyTypes)!);
                var dictLocal = emitIl.DeclareLocal(_types.DictionaryStringObject);
                emitIl.Emit(OpCodes.Stloc, dictLocal);

                // critical = (double)(Flags & 0x80)
                emitIl.Emit(OpCodes.Ldloc, dictLocal);
                emitIl.Emit(OpCodes.Ldstr, "critical");
                emitIl.Emit(OpCodes.Ldloc, recordLocal);
                emitIl.Emit(OpCodes.Callvirt, typeof(CaaRecord).GetProperty("Flags")!.GetGetMethod()!);
                emitIl.Emit(OpCodes.Ldc_I4, 0x80);
                emitIl.Emit(OpCodes.And);
                emitIl.Emit(OpCodes.Conv_R8);
                emitIl.Emit(OpCodes.Box, typeof(double));
                emitIl.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item")!);

                // dict[tag] = value
                emitIl.Emit(OpCodes.Ldloc, dictLocal);
                emitIl.Emit(OpCodes.Ldloc, recordLocal);
                emitIl.Emit(OpCodes.Callvirt, typeof(CaaRecord).GetProperty("Tag")!.GetGetMethod()!);
                emitIl.Emit(OpCodes.Ldloc, recordLocal);
                emitIl.Emit(OpCodes.Callvirt, typeof(CaaRecord).GetProperty("Value")!.GetGetMethod()!);
                emitIl.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item")!);

                emitIl.Emit(OpCodes.Ldloc, listLocal);
                emitIl.Emit(OpCodes.Ldloc, dictLocal);
                emitIl.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add")!);
            });
        il.Emit(OpCodes.Ldloc, caaList);
        il.Emit(OpCodes.Call, runtime.DnsConvertList);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Br, returnLabel);

        // === NAPTR section ===
        il.MarkLabel(naptrLabel);
        EmitDnsQueryCall(il, runtime, hostnameLocal, (int)QueryType.NAPTR);
        var naptrResponseLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, naptrResponseLocal);
        var naptrList = EmitDnsAnswerIteration(il, runtime, typeof(NAPtrRecord), naptrResponseLocal, hostnameLocal, "resolveNaptr",
            (emitIl, recordLocal, listLocal) =>
            {
                emitIl.Emit(OpCodes.Newobj, _types.DictionaryStringObject.GetConstructor(Type.EmptyTypes)!);
                var dictLocal = emitIl.DeclareLocal(_types.DictionaryStringObject);
                emitIl.Emit(OpCodes.Stloc, dictLocal);

                // flags
                emitIl.Emit(OpCodes.Ldloc, dictLocal);
                emitIl.Emit(OpCodes.Ldstr, "flags");
                emitIl.Emit(OpCodes.Ldloc, recordLocal);
                emitIl.Emit(OpCodes.Callvirt, typeof(NAPtrRecord).GetProperty("Flags")!.GetGetMethod()!);
                emitIl.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item")!);

                // service
                emitIl.Emit(OpCodes.Ldloc, dictLocal);
                emitIl.Emit(OpCodes.Ldstr, "service");
                emitIl.Emit(OpCodes.Ldloc, recordLocal);
                emitIl.Emit(OpCodes.Callvirt, typeof(NAPtrRecord).GetProperty("Services")!.GetGetMethod()!);
                emitIl.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item")!);

                // regexp
                emitIl.Emit(OpCodes.Ldloc, dictLocal);
                emitIl.Emit(OpCodes.Ldstr, "regexp");
                emitIl.Emit(OpCodes.Ldloc, recordLocal);
                emitIl.Emit(OpCodes.Callvirt, typeof(NAPtrRecord).GetProperty("RegularExpression")!.GetGetMethod()!);
                emitIl.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item")!);

                // replacement = Replacement.Value.TrimEnd('.')
                emitIl.Emit(OpCodes.Ldloc, dictLocal);
                emitIl.Emit(OpCodes.Ldstr, "replacement");
                emitIl.Emit(OpCodes.Ldloc, recordLocal);
                EmitDnsStringProperty(emitIl, typeof(NAPtrRecord), "Replacement");
                emitIl.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item")!);

                // order = (double)Order
                emitIl.Emit(OpCodes.Ldloc, dictLocal);
                emitIl.Emit(OpCodes.Ldstr, "order");
                emitIl.Emit(OpCodes.Ldloc, recordLocal);
                emitIl.Emit(OpCodes.Callvirt, typeof(NAPtrRecord).GetProperty("Order")!.GetGetMethod()!);
                emitIl.Emit(OpCodes.Conv_R8);
                emitIl.Emit(OpCodes.Box, typeof(double));
                emitIl.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item")!);

                // preference = (double)Preference
                emitIl.Emit(OpCodes.Ldloc, dictLocal);
                emitIl.Emit(OpCodes.Ldstr, "preference");
                emitIl.Emit(OpCodes.Ldloc, recordLocal);
                emitIl.Emit(OpCodes.Callvirt, typeof(NAPtrRecord).GetProperty("Preference")!.GetGetMethod()!);
                emitIl.Emit(OpCodes.Conv_R8);
                emitIl.Emit(OpCodes.Box, typeof(double));
                emitIl.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item")!);

                emitIl.Emit(OpCodes.Ldloc, listLocal);
                emitIl.Emit(OpCodes.Ldloc, dictLocal);
                emitIl.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add")!);
            });
        il.Emit(OpCodes.Ldloc, naptrList);
        il.Emit(OpCodes.Call, runtime.DnsConvertList);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Br, returnLabel);

        // === A section (uses System.Net.Dns) ===
        il.MarkLabel(aLabel);
        EmitDnsResolveAddresses(il, runtime, hostnameLocal, resultLocal, AddressFamily.InterNetwork);
        il.Emit(OpCodes.Br, returnLabel);

        // === AAAA section (uses System.Net.Dns) ===
        il.MarkLabel(aaaaLabel);
        EmitDnsResolveAddresses(il, runtime, hostnameLocal, resultLocal, AddressFamily.InterNetworkV6);
        il.Emit(OpCodes.Br, returnLabel);

        // === Unknown rrtype ===
        il.MarkLabel(unknownLabel);
        il.Emit(OpCodes.Ldstr, "Runtime Error: dns.resolve unknown rrtype: ");
        il.Emit(OpCodes.Ldloc, rrtypeLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "Concat", _types.String, _types.String));
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);

        // === Return ===
        il.MarkLabel(returnLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits an rrtype string comparison and branch.
    /// </summary>
    private static void EmitRrtypeCheck(ILGenerator il, LocalBuilder rrtypeLocal, string value, Label target, MethodInfo strEquals)
    {
        il.Emit(OpCodes.Ldloc, rrtypeLocal);
        il.Emit(OpCodes.Ldstr, value);
        il.Emit(OpCodes.Callvirt, strEquals);
        il.Emit(OpCodes.Brtrue, target);
    }

    /// <summary>
    /// Emits a call to DnsDoQuery(hostname, queryType) and leaves the result on the stack.
    /// </summary>
    private void EmitDnsQueryCall(ILGenerator il, EmittedRuntime runtime, LocalBuilder hostnameLocal, int queryType)
    {
        il.Emit(OpCodes.Ldloc, hostnameLocal);
        il.Emit(OpCodes.Ldc_I4, queryType);
        il.Emit(OpCodes.Call, runtime.DnsDoQuery);
    }

    /// <summary>
    /// Emits IL to iterate DNS answers, filter by record type, and build a result list.
    /// The emitExtraction callback emits IL to process a single matched record.
    /// </summary>
    /// <returns>LocalBuilder for the result List&lt;object?&gt;</returns>
    private LocalBuilder EmitDnsAnswerIteration(ILGenerator il, EmittedRuntime runtime,
        Type recordType, LocalBuilder responseLocal, LocalBuilder hostnameLocal,
        string methodName, Action<ILGenerator, LocalBuilder, LocalBuilder> emitExtraction)
    {
        // Get Answers from IDnsQueryResponse
        var answersLocal = il.DeclareLocal(typeof(IReadOnlyList<DnsResourceRecord>));
        il.Emit(OpCodes.Ldloc, responseLocal);
        il.Emit(OpCodes.Castclass, typeof(IDnsQueryResponse));
        il.Emit(OpCodes.Callvirt, typeof(IDnsQueryResponse).GetProperty("Answers")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, answersLocal);

        // Get count
        var countLocal = il.DeclareLocal(typeof(int));
        il.Emit(OpCodes.Ldloc, answersLocal);
        il.Emit(OpCodes.Callvirt, typeof(IReadOnlyCollection<DnsResourceRecord>).GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, countLocal);

        // Create result list
        var resultList = il.DeclareLocal(_types.ListOfObject);
        il.Emit(OpCodes.Newobj, _types.ListOfObject.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultList);

        // Loop index
        var indexLocal = il.DeclareLocal(typeof(int));
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, countLocal);
        il.Emit(OpCodes.Bge, loopEnd);

        // answers[i]
        il.Emit(OpCodes.Ldloc, answersLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, typeof(IReadOnlyList<DnsResourceRecord>).GetMethod("get_Item")!);

        // isinst recordType
        il.Emit(OpCodes.Isinst, recordType);
        var recordLocal = il.DeclareLocal(recordType);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Stloc, recordLocal);
        var skipLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, skipLabel);

        // Extract and add to result list
        emitExtraction(il, recordLocal, resultList);

        il.MarkLabel(skipLabel);

        // i++
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);

        // Check empty
        il.Emit(OpCodes.Ldloc, resultList);
        il.Emit(OpCodes.Callvirt, _types.GetPropertyGetter(_types.ListOfObject, "Count"));
        var notEmptyLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, notEmptyLabel);

        il.Emit(OpCodes.Ldstr, $"Runtime Error: dns.{methodName} ENODATA ");
        il.Emit(OpCodes.Ldloc, hostnameLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "Concat", _types.String, _types.String));
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);

        il.MarkLabel(notEmptyLabel);
        return resultList;
    }

    /// <summary>
    /// Emits IL for record types that produce a list of strings from a DnsString property
    /// (CNAME, NS, PTR). Gets propertyName.Value.TrimEnd('.') for each matching record.
    /// </summary>
    private LocalBuilder EmitDnsStringRecordIteration(ILGenerator il, EmittedRuntime runtime,
        Type recordType, string dnsStringPropertyName,
        LocalBuilder responseLocal, LocalBuilder hostnameLocal, string methodName)
    {
        return EmitDnsAnswerIteration(il, runtime, recordType, responseLocal, hostnameLocal, methodName,
            (emitIl, recordLocal, listLocal) =>
            {
                emitIl.Emit(OpCodes.Ldloc, listLocal);
                emitIl.Emit(OpCodes.Ldloc, recordLocal);
                EmitDnsStringProperty(emitIl, recordType, dnsStringPropertyName);
                emitIl.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add")!);
            });
    }

    /// <summary>
    /// Emits IL to get a DnsString property, read .Value, and TrimEnd('.').
    /// Assumes the record object is already on the stack.
    /// </summary>
    private static void EmitDnsStringProperty(ILGenerator il, Type recordType, string propertyName)
    {
        il.Emit(OpCodes.Callvirt, recordType.GetProperty(propertyName)!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, typeof(DnsString).GetProperty("Value")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, typeof(char));
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4, (int)'.');
        il.Emit(OpCodes.Stelem_I2);
        il.Emit(OpCodes.Callvirt, typeof(string).GetMethod("TrimEnd", [typeof(char[])])!);
    }

    /// <summary>
    /// Emits SOA record processing. SOA returns a single $Object, not an array.
    /// </summary>
    private void EmitDnsSoaProcessing(ILGenerator il, EmittedRuntime runtime,
        LocalBuilder responseLocal, LocalBuilder hostnameLocal, LocalBuilder resultLocal)
    {
        // Get Answers
        var answersLocal = il.DeclareLocal(typeof(IReadOnlyList<DnsResourceRecord>));
        il.Emit(OpCodes.Ldloc, responseLocal);
        il.Emit(OpCodes.Castclass, typeof(IDnsQueryResponse));
        il.Emit(OpCodes.Callvirt, typeof(IDnsQueryResponse).GetProperty("Answers")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, answersLocal);

        var countLocal = il.DeclareLocal(typeof(int));
        il.Emit(OpCodes.Ldloc, answersLocal);
        il.Emit(OpCodes.Callvirt, typeof(IReadOnlyCollection<DnsResourceRecord>).GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, countLocal);

        // Find first SoaRecord
        var foundLocal = il.DeclareLocal(typeof(bool));
        var indexLocal = il.DeclareLocal(typeof(int));
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, foundLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();
        var foundLabel = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, countLocal);
        il.Emit(OpCodes.Bge, loopEnd);

        il.Emit(OpCodes.Ldloc, answersLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, typeof(IReadOnlyList<DnsResourceRecord>).GetMethod("get_Item")!);
        il.Emit(OpCodes.Isinst, typeof(SoaRecord));
        var soaLocal = il.DeclareLocal(typeof(SoaRecord));
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Stloc, soaLocal);
        il.Emit(OpCodes.Brtrue, foundLabel);

        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        // Not found -> throw ENODATA
        il.Emit(OpCodes.Ldstr, "Runtime Error: dns.resolveSoa ENODATA ");
        il.Emit(OpCodes.Ldloc, hostnameLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "Concat", _types.String, _types.String));
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);

        il.MarkLabel(foundLabel);
        // Build dictionary from SoaRecord
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObject.GetConstructor(Type.EmptyTypes)!);
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, dictLocal);

        // nsname = MName.Value.TrimEnd('.')
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "nsname");
        il.Emit(OpCodes.Ldloc, soaLocal);
        EmitDnsStringProperty(il, typeof(SoaRecord), "MName");
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item")!);

        // hostmaster = RName.Value.TrimEnd('.')
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "hostmaster");
        il.Emit(OpCodes.Ldloc, soaLocal);
        EmitDnsStringProperty(il, typeof(SoaRecord), "RName");
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item")!);

        // serial, refresh, retry, expire, minttl — all uint -> double
        foreach (var (key, prop) in new[] {
            ("serial", "Serial"), ("refresh", "Refresh"), ("retry", "Retry"),
            ("expire", "Expire"), ("minttl", "Minimum") })
        {
            il.Emit(OpCodes.Ldloc, dictLocal);
            il.Emit(OpCodes.Ldstr, key);
            il.Emit(OpCodes.Ldloc, soaLocal);
            il.Emit(OpCodes.Callvirt, typeof(SoaRecord).GetProperty(prop)!.GetGetMethod()!);
            il.Emit(OpCodes.Conv_R8);
            il.Emit(OpCodes.Box, typeof(double));
            il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item")!);
        }

        // Wrap as $Object
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Call, runtime.CreateObject);
        il.Emit(OpCodes.Stloc, resultLocal);
    }

    /// <summary>
    /// Emits IL for A/AAAA resolution using System.Net.Dns.GetHostEntry.
    /// Stores $Array result in resultLocal.
    /// </summary>
    private void EmitDnsResolveAddresses(ILGenerator il, EmittedRuntime runtime,
        LocalBuilder hostnameLocal, LocalBuilder resultLocal, AddressFamily family)
    {
        // Dns.GetHostEntry(hostname)
        il.Emit(OpCodes.Ldloc, hostnameLocal);
        il.Emit(OpCodes.Call, typeof(Dns).GetMethod("GetHostEntry", [typeof(string)])!);
        var hostEntryLocal = il.DeclareLocal(typeof(IPHostEntry));
        il.Emit(OpCodes.Stloc, hostEntryLocal);

        // Get AddressList
        il.Emit(OpCodes.Ldloc, hostEntryLocal);
        il.Emit(OpCodes.Callvirt, typeof(IPHostEntry).GetProperty("AddressList")!.GetGetMethod()!);
        var addressListLocal = il.DeclareLocal(typeof(IPAddress[]));
        il.Emit(OpCodes.Stloc, addressListLocal);

        // Build list of matching addresses
        il.Emit(OpCodes.Newobj, _types.ListOfObject.GetConstructor(Type.EmptyTypes)!);
        var listLocal = il.DeclareLocal(_types.ListOfObject);
        il.Emit(OpCodes.Stloc, listLocal);

        var indexLocal = il.DeclareLocal(typeof(int));
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);
        var loopStart = il.DefineLabel();
        var loopCond = il.DefineLabel();
        il.Emit(OpCodes.Br, loopCond);

        il.MarkLabel(loopStart);
        // Check address family
        il.Emit(OpCodes.Ldloc, addressListLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Callvirt, typeof(IPAddress).GetProperty("AddressFamily")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4, (int)family);
        var skipLabel = il.DefineLabel();
        il.Emit(OpCodes.Bne_Un, skipLabel);

        // Match - add to list
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Ldloc, addressListLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(typeof(IPAddress), "ToString"));
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add")!);

        il.MarkLabel(skipLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);

        il.MarkLabel(loopCond);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, addressListLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Blt, loopStart);

        // Check if empty
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, _types.GetPropertyGetter(_types.ListOfObject, "Count"));
        var notEmptyLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, notEmptyLabel);

        il.Emit(OpCodes.Ldc_I4, (int)SocketError.HostNotFound);
        il.Emit(OpCodes.Newobj, typeof(SocketException).GetConstructor([typeof(int)])!);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(notEmptyLabel);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Newobj, runtime.TSArrayCtor);
        il.Emit(OpCodes.Stloc, resultLocal);
    }

    /// <summary>
    /// Emits DnsDoQuery: shared helper that creates a LookupClient, performs a DNS query,
    /// checks for errors, and returns the IDnsQueryResponse.
    /// Signature: object DnsDoQuery(string hostname, int queryType)
    /// Uses DnsClient.dll types directly — no SharpTS.dll dependency.
    /// </summary>
    private void EmitDnsDoQuery(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DnsDoQuery",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String, _types.Int32]);
        runtime.DnsDoQuery = method;

        var il = method.GetILGenerator();

        // var client = new LookupClient();
        var clientLocal = il.DeclareLocal(typeof(LookupClient));
        il.Emit(OpCodes.Newobj, typeof(LookupClient).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, clientLocal);

        // var result = client.Query(hostname, (QueryType)queryType, QueryClass.IN);
        il.Emit(OpCodes.Ldloc, clientLocal);
        il.Emit(OpCodes.Ldarg_0); // hostname
        il.Emit(OpCodes.Ldarg_1); // queryType (int)
        // QueryType is an enum backed by int, we need to convert
        // QueryClass.IN = 1
        il.Emit(OpCodes.Ldc_I4_1); // QueryClass.IN
        il.Emit(OpCodes.Callvirt, typeof(LookupClient).GetMethod("Query",
            [typeof(string), typeof(QueryType), typeof(QueryClass)])!);
        var resultLocal = il.DeclareLocal(typeof(IDnsQueryResponse));
        il.Emit(OpCodes.Stloc, resultLocal);

        // if (result.HasError)
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Callvirt, typeof(IDnsQueryResponse).GetProperty("HasError")!.GetGetMethod()!);
        var noErrorLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, noErrorLabel);

        // Get error code from result.Header.ResponseCode
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Callvirt, typeof(IDnsQueryResponse).GetProperty("Header")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, typeof(DnsResponseHeader).GetProperty("ResponseCode")!.GetGetMethod()!);
        var responseCodeLocal = il.DeclareLocal(typeof(DnsHeaderResponseCode));
        il.Emit(OpCodes.Stloc, responseCodeLocal);

        // Map ResponseCode to error string
        var errorCodeLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldstr, "EAI_FAIL"); // default
        il.Emit(OpCodes.Stloc, errorCodeLocal);

        EmitResponseCodeCheck(il, responseCodeLocal, errorCodeLocal, DnsHeaderResponseCode.NotExistentDomain, "ENOTFOUND");
        EmitResponseCodeCheck(il, responseCodeLocal, errorCodeLocal, DnsHeaderResponseCode.ServerFailure, "ESERVFAIL");
        EmitResponseCodeCheck(il, responseCodeLocal, errorCodeLocal, DnsHeaderResponseCode.Refused, "EREFUSED");
        EmitResponseCodeCheck(il, responseCodeLocal, errorCodeLocal, DnsHeaderResponseCode.FormatError, "EFORMERR");
        EmitResponseCodeCheck(il, responseCodeLocal, errorCodeLocal, DnsHeaderResponseCode.NotImplemented, "ENOTIMP");

        // throw new Exception("Runtime Error: dns.resolve " + code + " " + hostname)
        il.Emit(OpCodes.Ldstr, "Runtime Error: dns.resolve ");
        il.Emit(OpCodes.Ldloc, errorCodeLocal);
        il.Emit(OpCodes.Ldstr, " ");
        il.Emit(OpCodes.Ldarg_0); // hostname
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "Concat", _types.String, _types.String, _types.String, _types.String));
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);

        il.MarkLabel(noErrorLabel);

        // return result (as object)
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits a single ResponseCode comparison and error code assignment.
    /// </summary>
    private static void EmitResponseCodeCheck(ILGenerator il, LocalBuilder responseCodeLocal,
        LocalBuilder errorCodeLocal, DnsHeaderResponseCode code, string errorStr)
    {
        il.Emit(OpCodes.Ldloc, responseCodeLocal);
        il.Emit(OpCodes.Ldc_I4, (int)code);
        var skipLabel = il.DefineLabel();
        il.Emit(OpCodes.Bne_Un, skipLabel);
        il.Emit(OpCodes.Ldstr, errorStr);
        il.Emit(OpCodes.Stloc, errorCodeLocal);
        il.MarkLabel(skipLabel);
    }

    /// <summary>
    /// Emits DnsConvertList: converts a List&lt;object?&gt; of raw DNS results to $Array/$Object.
    /// Inner List elements become $Array, inner Dictionary elements become $Object, strings stay as-is.
    /// </summary>
    private void EmitDnsConvertList(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DnsConvertList",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ListOfObject]);
        runtime.DnsConvertList = method;

        var il = method.GetILGenerator();

        // Create output List<object?>
        il.Emit(OpCodes.Newobj, _types.ListOfObject.GetConstructor(Type.EmptyTypes)!);
        var outListLocal = il.DeclareLocal(_types.ListOfObject); // 0
        il.Emit(OpCodes.Stloc, outListLocal);

        // for (int i = 0; i < input.Count; i++)
        var indexLocal = il.DeclareLocal(typeof(int)); // 1
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopCond = il.DefineLabel();
        il.Emit(OpCodes.Br, loopCond);

        il.MarkLabel(loopStart);

        // var item = input[i]
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "get_Item", _types.Int32));
        var itemLocal = il.DeclareLocal(_types.Object); // 2
        il.Emit(OpCodes.Stloc, itemLocal);

        // if (item is Dictionary<string, object?>) -> CreateObject
        il.Emit(OpCodes.Ldloc, itemLocal);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        var notInnerDictLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notInnerDictLabel);

        il.Emit(OpCodes.Ldloc, outListLocal);
        il.Emit(OpCodes.Ldloc, itemLocal);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Call, runtime.CreateObject);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add")!);
        var continueLabel = il.DefineLabel();
        il.Emit(OpCodes.Br, continueLabel);

        il.MarkLabel(notInnerDictLabel);

        // if (item is List<object?>) -> new $Array
        il.Emit(OpCodes.Ldloc, itemLocal);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        var notInnerListLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notInnerListLabel);

        il.Emit(OpCodes.Ldloc, outListLocal);
        il.Emit(OpCodes.Ldloc, itemLocal);
        il.Emit(OpCodes.Castclass, _types.ListOfObject);
        il.Emit(OpCodes.Newobj, runtime.TSArrayCtor);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add")!);
        il.Emit(OpCodes.Br, continueLabel);

        il.MarkLabel(notInnerListLabel);

        // else: add as-is (string, etc.)
        il.Emit(OpCodes.Ldloc, outListLocal);
        il.Emit(OpCodes.Ldloc, itemLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add")!);

        il.MarkLabel(continueLabel);

        // i++
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);

        il.MarkLabel(loopCond);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetPropertyGetter(_types.ListOfObject, "Count"));
        il.Emit(OpCodes.Blt, loopStart);

        // return new $Array(outList)
        il.Emit(OpCodes.Ldloc, outListLocal);
        il.Emit(OpCodes.Newobj, runtime.TSArrayCtor);
        il.Emit(OpCodes.Ret);
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
    /// Routes to DnsResolveRecord for non-A/AAAA types, uses GetHostEntry for A/AAAA.
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
        var rrtypeLocal = il.DeclareLocal(_types.Object); // rrtype or null
        var endLabel = il.DefineLabel();

        // If arg2 != null, callback=arg2, rrtype=arg1 (3-arg form)
        // If arg2 == null, callback=arg1, rrtype=null (2-arg form, default "A")
        var threeArgLabel = il.DefineLabel();
        var afterCallbackLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Brtrue, threeArgLabel);

        // 2-arg form: callback=arg1, rrtype=null
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stloc, callbackLocal);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, rrtypeLocal);
        il.Emit(OpCodes.Br, afterCallbackLabel);

        il.MarkLabel(threeArgLabel);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Stloc, callbackLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stloc, rrtypeLocal);

        il.MarkLabel(afterCallbackLabel);

        // Check if rrtype is a non-A/AAAA string -> route to DnsResolveRecord
        var useGetHostEntryLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, rrtypeLocal);
        il.Emit(OpCodes.Brfalse, useGetHostEntryLabel); // null -> default A

        // Check if rrtype is a string
        il.Emit(OpCodes.Ldloc, rrtypeLocal);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brfalse, useGetHostEntryLabel); // not a string -> default A

        // Convert to upper and check
        il.Emit(OpCodes.Ldloc, rrtypeLocal);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.String, "ToUpperInvariant"));
        var upperRrtypeLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Stloc, upperRrtypeLocal);

        var strEquals = typeof(string).GetMethod("Equals", [typeof(string)])!;

        // If "A" or "AAAA" -> use GetHostEntry
        il.Emit(OpCodes.Ldloc, upperRrtypeLocal);
        il.Emit(OpCodes.Ldstr, "A");
        il.Emit(OpCodes.Callvirt, strEquals);
        il.Emit(OpCodes.Brtrue, useGetHostEntryLabel);

        il.Emit(OpCodes.Ldloc, upperRrtypeLocal);
        il.Emit(OpCodes.Ldstr, "AAAA");
        il.Emit(OpCodes.Callvirt, strEquals);
        il.Emit(OpCodes.Brtrue, useGetHostEntryLabel);

        // Non-A/AAAA rrtype -> use DnsResolveRecord
        il.BeginExceptionBlock();

        il.Emit(OpCodes.Ldarg_0); // hostname
        il.Emit(OpCodes.Ldloc, rrtypeLocal); // rrtype
        il.Emit(OpCodes.Call, runtime.DnsResolveRecord);
        var recordResultLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, recordResultLocal);

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
        il.Emit(OpCodes.Ldloc, recordResultLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvoke);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Leave, endLabel);

        il.BeginCatchBlock(typeof(Exception));
        var exLocal1 = il.DeclareLocal(typeof(Exception));
        il.Emit(OpCodes.Stloc, exLocal1);

        il.Emit(OpCodes.Ldloc, callbackLocal);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, exLocal1);
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
        il.Emit(OpCodes.Br, endLabel);

        // === GetHostEntry path (A/AAAA or default) ===
        il.MarkLabel(useGetHostEntryLabel);

        il.BeginExceptionBlock();

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

    /// <summary>
    /// Emits async callback wrappers for DNS record type methods:
    /// resolveMx, resolveTxt, resolveSrv, resolveCname, resolveNs, resolveSoa, resolvePtr, resolveCaa, resolveNaptr.
    /// Each takes (hostname, callback) and calls DnsResolveRecord(hostname, rrtype).
    /// </summary>
    private void EmitDnsRecordTypeWrappers(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var recordTypes = new[]
        {
            ("resolveMx", "MX"),
            ("resolveTxt", "TXT"),
            ("resolveSrv", "SRV"),
            ("resolveCname", "CNAME"),
            ("resolveNs", "NS"),
            ("resolveSoa", "SOA"),
            ("resolvePtr", "PTR"),
            ("resolveCaa", "CAA"),
            ("resolveNaptr", "NAPTR")
        };

        foreach (var (methodName, rrtype) in recordTypes)
        {
            EmitDnsRecordTypeWrapper(typeBuilder, runtime, methodName, rrtype);
        }
    }

    /// <summary>
    /// Emits a single DNS record type callback wrapper.
    /// Pattern: (hostname, callback) -> calls DnsResolveRecord(hostname, rrtype), then callback(null, result) or callback(error, null).
    /// </summary>
    private void EmitDnsRecordTypeWrapper(TypeBuilder typeBuilder, EmittedRuntime runtime, string methodName, string rrtype)
    {
        var method = typeBuilder.DefineMethod(
            "DnsWrapper_" + methodName,
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]);

        var il = method.GetILGenerator();
        var resultLocal = il.DeclareLocal(_types.Object);
        var endLabel = il.DefineLabel();

        il.BeginExceptionBlock();

        // Call DnsResolveRecord(hostname, rrtype)
        il.Emit(OpCodes.Ldarg_0); // hostname
        il.Emit(OpCodes.Ldstr, rrtype); // rrtype constant
        il.Emit(OpCodes.Call, runtime.DnsResolveRecord);
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

        // catch
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

        runtime.RegisterBuiltInModuleMethod("dns", methodName, method);
    }
}
