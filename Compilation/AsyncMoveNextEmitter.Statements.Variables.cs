using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class AsyncMoveNextEmitter
{
    protected override FieldBuilder? GetHoistedVariableField(string name) => _builder.GetVariableField(name);
}
