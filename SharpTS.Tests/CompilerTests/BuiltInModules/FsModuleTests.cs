using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.CompilerTests.BuiltInModules;

/// <summary>
/// All tests migrated to SharedTests/BuiltInModules/FsModuleTests.cs
/// to run against both interpreter and compiler.
/// </summary>
public class FsModuleTests
{
    // Migrated to SharedTests/BuiltInModules/FsModuleTests.cs:
    // - Fs_ExistsSync_ReturnsTrueForExistingFile
    // - Fs_ExistsSync_ReturnsFalseForNonexistentFile
    // - Fs_WriteFileSync_And_ReadFileSync_WorkTogether
    // - Fs_AppendFileSync_AppendsToFile
    // - Fs_MkdirSync_And_RmdirSync_WorkTogether
    // - Fs_ReaddirSync_ListsDirectoryContents
    // - Fs_StatSync_ReturnsFileInfo
    // - Fs_StatSync_ReturnsDirectoryInfo
    // - Fs_CopyFileSync_CopiesFile
    // - Fs_RenameSync_RenamesFile
    // - Fs_UnlinkSync_DeletesFile
    // - Fs_AccessSync_DoesNotThrowForExistingFile
    // - Fs_AccessSync_ThrowsForNonexistentFile
    // - Fs_RmdirSync_WithRecursive_DeletesNestedDirectories
    // - Fs_Constants_ExportsAccessConstants
    // - Fs_TruncateSync_TruncatesFile
    // - Fs_TruncateSync_ExtendsFileWithZeros
    // - Fs_TruncateSync_ThrowsForNonexistentFile
    // - Fs_SymlinkSync_CreatesSymbolicLink
    // - Fs_RealpathSync_ResolvesAbsolutePath
    // - Fs_RealpathSync_ThrowsForNonexistentFile
    // - Fs_UtimesSync_SetsFileTimes
    // - Fs_LstatSync_ReturnsSymlinkInfo
    // - Fs_ReaddirSync_WithFileTypes_ReturnsDirentObjects
    // - Fs_ChmodSync_DoesNotThrowOnUnix
    // - Fs_ReadlinkSync_ThrowsForNonSymlink
    // - Fs_OpenSync_ReturnsFileDescriptor
    // - Fs_CloseSync_ClosesDescriptor
    // - Fs_CloseSync_ThrowsForInvalidFd
    // - Fs_ReadSync_ReadsIntoBuffer
    // - Fs_WriteSync_WritesFromBuffer
    // - Fs_FstatSync_ReturnsStats
    // - Fs_FtruncateSync_TruncatesFile
    // - Fs_MkdtempSync_CreatesUniqueDirectory
    // - Fs_ReaddirSync_Recursive_ListsAllEntries
    // - Fs_OpendirSync_ReturnsDir
    // - Fs_Dir_ReadSync_ReturnsNullWhenDone
    // - Fs_LinkSync_CreatesHardLink
    // - Fs_LinkSync_ThrowsForMissingSource
    // - Fs_LinkSync_ThrowsForExistingDest
}
