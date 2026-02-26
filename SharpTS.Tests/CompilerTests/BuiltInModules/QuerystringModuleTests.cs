using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.CompilerTests.BuiltInModules;

/// <summary>
/// All tests migrated to SharedTests/BuiltInModules/QuerystringModuleTests.cs
/// to run against both interpreter and compiler.
/// </summary>
public class QuerystringModuleTests
{
    // Migrated to SharedTests/BuiltInModules/QuerystringModuleTests.cs:
    // - Querystring_Parse_ParsesSimpleString
    // - Querystring_Parse_HandlesUrlEncoding
    // - Querystring_Parse_HandlesPlusAsSpace
    // - Querystring_Parse_HandlesEmptyValue
    // - Querystring_Parse_CustomSeparator
    // - Querystring_Stringify_CreatesQueryString
    // - Querystring_Stringify_EncodesSpecialChars
    // - Querystring_Escape_EncodesString
    // - Querystring_Unescape_DecodesString
    // - Querystring_DecodeAlias_WorksLikeParse
    // - Querystring_EncodeAlias_WorksLikeStringify
    // - Querystring_NamespaceImport_Works
    // - Querystring_RoundTrip_PreservesData
}
