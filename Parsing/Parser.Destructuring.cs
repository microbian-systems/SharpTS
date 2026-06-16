namespace SharpTS.Parsing;

public partial class Parser
{
    // ============== DESTRUCTURING PATTERN PARSING ==============

    private DestructurePattern ParseDestructurePattern()
    {
        if (Match(TokenType.LEFT_BRACKET))
            return ParseArrayPattern();
        if (Match(TokenType.LEFT_BRACE))
            return ParseObjectPattern();
        if (Match(TokenType.DOT_DOT_DOT))
        {
            Token restName = ConsumeIdentifierName("Expect identifier after '...'.");
            return new RestPattern(restName);
        }

        Token patternName = ConsumeIdentifierName("Expect identifier in pattern.");
        Expr? defaultValue = Match(TokenType.EQUAL) ? Expression() : null;
        return new IdentifierPattern(patternName, defaultValue);
    }

    private ArrayPattern ParseArrayPattern()
    {
        int line = Previous().Line;
        List<ArrayPatternElement> elements = [];

        while (!Check(TokenType.RIGHT_BRACKET) && !IsAtEnd())
        {
            if (Check(TokenType.COMMA))
            {
                // Hole in array: [a, , c]
                elements.Add(new ArrayPatternElement(null, IsHole: true));
            }
            else
            {
                elements.Add(new ArrayPatternElement(ParseDestructurePattern(), IsHole: false));
            }

            if (!Check(TokenType.RIGHT_BRACKET))
            {
                Consume(TokenType.COMMA, "Expect ',' between array pattern elements.");
            }
        }

        Consume(TokenType.RIGHT_BRACKET, "Expect ']' after array pattern.");
        return new ArrayPattern(elements, line);
    }

    private ObjectPattern ParseObjectPattern()
    {
        int line = Previous().Line;
        List<ObjectPatternProperty> properties = [];

        while (!Check(TokenType.RIGHT_BRACE) && !IsAtEnd())
        {
            // Handle rest pattern: { ...rest }
            if (Match(TokenType.DOT_DOT_DOT))
            {
                Token restName = ConsumeIdentifierName("Expect identifier after '...'.");
                properties.Add(new ObjectPatternProperty(restName, new RestPattern(restName), null));
                // Rest must be last, so break out of loop
                break;
            }

            // Property-key position accepts any keyword (JS semantics).
            Token key = ConsumePropertyName("Expect property name.");
            DestructurePattern value;
            Expr? defaultValue = null;

            if (Match(TokenType.COLON))
            {
                // Rename or nested: { x: newName } or { x: { nested } }
                if (Check(TokenType.LEFT_BRACE) || Check(TokenType.LEFT_BRACKET))
                {
                    value = ParseDestructurePattern();
                }
                else
                {
                    Token rename = ConsumeIdentifierName("Expect identifier after ':'.");
                    if (Match(TokenType.EQUAL))
                        defaultValue = Expression();
                    value = new IdentifierPattern(rename, defaultValue);
                }
            }
            else
            {
                // Shorthand: { x } or { x = default }
                if (Match(TokenType.EQUAL))
                    defaultValue = Expression();
                value = new IdentifierPattern(key, defaultValue);
            }

            properties.Add(new ObjectPatternProperty(key, value, defaultValue));

            if (!Check(TokenType.RIGHT_BRACE))
            {
                Consume(TokenType.COMMA, "Expect ',' between object pattern properties.");
            }
        }

        Consume(TokenType.RIGHT_BRACE, "Expect '}' after object pattern.");
        return new ObjectPattern(properties, line);
    }

    // ============== DESTRUCTURING DECLARATIONS ==============

    private Stmt DestructureArrayDeclaration()
    {
        Consume(TokenType.LEFT_BRACKET, "Expect '[' for array destructuring.");
        ArrayPattern pattern = ParseArrayPattern();

        // Optional type annotation (ignored for now, inferred from initializer)
        if (Match(TokenType.COLON))
            ParseTypeAnnotation();

        Consume(TokenType.EQUAL, "Expect '=' after destructuring pattern.");
        Expr initializer = Expression();
        ConsumeSemicolon("Expect ';' after variable declaration.");

        return DesugarArrayPattern(pattern, initializer);
    }

    private Stmt DestructureObjectDeclaration()
    {
        Consume(TokenType.LEFT_BRACE, "Expect '{' for object destructuring.");
        ObjectPattern pattern = ParseObjectPattern();

        // Optional type annotation (ignored for now, inferred from initializer)
        if (Match(TokenType.COLON))
            ParseTypeAnnotation();

        Consume(TokenType.EQUAL, "Expect '=' after destructuring pattern.");
        Expr initializer = Expression();
        ConsumeSemicolon("Expect ';' after variable declaration.");

        return DesugarObjectPattern(pattern, initializer);
    }

    // ============== DESUGARING METHODS ==============

    private Stmt DesugarArrayPattern(ArrayPattern pattern, Expr initializer)
    {
        List<Stmt> statements = [];

        // const _dest0 = __arrayDestructure(initializer);
        // JS array destructuring follows the iterator protocol, not positional
        // indexing. The wrapper normalizes non-indexable iterables (generators,
        // Set, Map, objects with [Symbol.iterator]) into an array so the index
        // access below is spec-correct (#685); arrays/tuples/strings pass through
        // unchanged to keep the fast index path.
        Token temp = GenerateTempVar(pattern.Line);
        Expr normalizedInit = new Expr.Call(
            new Expr.Variable(new Token(TokenType.IDENTIFIER, "__arrayDestructure", null, pattern.Line)),
            new Token(TokenType.RIGHT_PAREN, ")", null, pattern.Line),
            null,
            [initializer]
        );
        statements.Add(new Stmt.Var(temp, null, normalizedInit));

        int index = 0;
        foreach (var element in pattern.Elements)
        {
            if (element.IsHole)
            {
                index++;
                continue;
            }

            if (element.Pattern is RestPattern rest)
            {
                // const rest = _dest0.slice(index);
                Expr sliceCall = new Expr.Call(
                    new Expr.Get(new Expr.Variable(temp),
                        new Token(TokenType.IDENTIFIER, "slice", null, pattern.Line)),
                    new Token(TokenType.RIGHT_PAREN, ")", null, pattern.Line),
                    null,
                    [new Expr.Literal((double)index)]
                );
                statements.Add(new Stmt.Var(rest.Name, null, sliceCall));
                break; // Rest must be last
            }

            // Access expression: _dest0[index]
            Expr accessExpr = new Expr.GetIndex(
                new Expr.Variable(temp),
                new Expr.Literal((double)index)
            );

            if (element.Pattern is IdentifierPattern id)
            {
                // Apply default value if present
                if (id.DefaultValue != null)
                {
                    accessExpr = new Expr.NullishCoalescing(accessExpr, id.DefaultValue);
                }
                statements.Add(new Stmt.Var(id.Name, null, accessExpr));
            }
            else if (element.Pattern is ArrayPattern nestedArray)
            {
                statements.Add(DesugarArrayPattern(nestedArray, accessExpr));
            }
            else if (element.Pattern is ObjectPattern nestedObj)
            {
                statements.Add(DesugarObjectPattern(nestedObj, accessExpr));
            }

            index++;
        }

        return new Stmt.Sequence(statements);
    }

    private Stmt DesugarObjectPattern(ObjectPattern pattern, Expr initializer)
    {
        List<Stmt> statements = [];
        List<string> usedKeys = [];

        // const _dest0 = initializer;
        Token temp = GenerateTempVar(pattern.Line);
        statements.Add(new Stmt.Var(temp, null, initializer));

        foreach (var prop in pattern.Properties)
        {
            // Handle rest pattern: const { x, ...rest } = obj
            if (prop.Value is RestPattern rest)
            {
                // Generate: const rest = __objectRest(_dest0, ["x", "y", ...usedKeys]);
                var excludeKeysExpr = new Expr.ArrayLiteral(
                    usedKeys.Select(k => new Expr.Literal(k) as Expr).ToList()
                );
                var restCall = new Expr.Call(
                    new Expr.Variable(new Token(TokenType.IDENTIFIER, "__objectRest", null, pattern.Line)),
                    new Token(TokenType.RIGHT_PAREN, ")", null, pattern.Line),
                    null,
                    [new Expr.Variable(temp), excludeKeysExpr]
                );
                statements.Add(new Stmt.Var(rest.Name, null, restCall));
                break; // Rest must be last
            }

            usedKeys.Add(prop.Key.Lexeme);

            // Access expression: _dest0.key
            Expr accessExpr = new Expr.Get(new Expr.Variable(temp), prop.Key);

            if (prop.Value is IdentifierPattern id)
            {
                // Apply default value if present
                Expr? defaultVal = prop.DefaultValue ?? id.DefaultValue;
                if (defaultVal != null)
                {
                    accessExpr = new Expr.NullishCoalescing(accessExpr, defaultVal);
                }
                statements.Add(new Stmt.Var(id.Name, null, accessExpr));
            }
            else if (prop.Value is ArrayPattern nestedArray)
            {
                statements.Add(DesugarArrayPattern(nestedArray, accessExpr));
            }
            else if (prop.Value is ObjectPattern nestedObj)
            {
                statements.Add(DesugarObjectPattern(nestedObj, accessExpr));
            }
        }

        return new Stmt.Sequence(statements);
    }

    // ============== ASSIGNMENT DESTRUCTURING (#754) ==============
    // `[a, b] = rhs` / `({a, b} = rhs)` assign to EXISTING l-values (not new bindings). The target has
    // already been parsed as an array/object literal (the eager-parse cover grammar); here it is
    // reinterpreted as a pattern and lowered into assignment statements that reuse the #685
    // iterator-protocol normalization (`__arrayDestructure`) and `__objectRest`. The rhs is evaluated
    // once into a temp and yielded as the expression's value (ECMA-262: an assignment evaluates to its
    // right-hand side, the ORIGINAL rhs — not the normalized array).

    /// <summary>True when an `=` target parsed as an array/object literal is an assignment-destructuring
    /// pattern (#754) rather than an ordinary assignment target.</summary>
    private static bool IsDestructuringAssignmentTarget(Expr target) =>
        Unwrap(target) is Expr.ArrayLiteral or Expr.ObjectLiteral;

    private static Expr Unwrap(Expr e) => e is Expr.Grouping g ? Unwrap(g.Expression) : e;

    private Expr BuildDestructuringAssignment(Expr pattern, Expr value, int line)
    {
        var stmts = new List<Stmt>();
        // _destN = rhs — evaluate the source exactly once; it is also the expression's result value.
        Token rhsTemp = GenerateTempVar(line);
        stmts.Add(new Stmt.Var(rhsTemp, null, value));
        LowerAssignmentTarget(pattern, new Expr.Variable(rhsTemp), stmts, line);
        return new Expr.DestructuringAssign(stmts, new Expr.Variable(rhsTemp));
    }

    private void LowerAssignmentTarget(Expr target, Expr source, List<Stmt> stmts, int line)
    {
        switch (Unwrap(target))
        {
            case Expr.ArrayLiteral arr: LowerArrayAssignmentTarget(arr, source, stmts, line); break;
            case Expr.ObjectLiteral obj: LowerObjectAssignmentTarget(obj, source, stmts, line); break;
            default: stmts.Add(new Stmt.Expression(MakeAssignmentExpr(target, source))); break;
        }
    }

    private void LowerArrayAssignmentTarget(Expr.ArrayLiteral arr, Expr source, List<Stmt> stmts, int line)
    {
        // _srcN = __arrayDestructure(source) — normalize any iterable to an indexable array (#685).
        Token srcTemp = GenerateTempVar(line);
        stmts.Add(new Stmt.Var(srcTemp, null, MakeArrayDestructureCall(source, line)));
        Expr src = new Expr.Variable(srcTemp);

        for (int i = 0; i < arr.Elements.Count; i++)
        {
            if (arr.IsHole(i)) continue;
            Expr element = Unwrap(arr.Elements[i]);

            if (element is Expr.Spread spread)
            {
                // Rest: target = _srcN.slice(i). Must be the final element.
                LowerAssignmentTarget(spread.Expression, MakeSliceCall(src, i, line), stmts, line);
                break;
            }

            LowerElementWithDefault(element, new Expr.GetIndex(src, new Expr.Literal((double)i)), stmts, line);
        }
    }

    private void LowerObjectAssignmentTarget(Expr.ObjectLiteral obj, Expr source, List<Stmt> stmts, int line)
    {
        // _srcN = source — object patterns read properties directly (no iterator normalization).
        Token srcTemp = GenerateTempVar(line);
        stmts.Add(new Stmt.Var(srcTemp, null, source));
        Expr src = new Expr.Variable(srcTemp);
        var usedKeys = new List<Expr>();

        foreach (var prop in obj.Properties)
        {
            if (prop.IsSpread)
            {
                // Rest: target = __objectRest(_srcN, [usedKeys]). Must be last.
                LowerAssignmentTarget(prop.Value, MakeObjectRestCall(src, usedKeys, line), stmts, line);
                break;
            }
            if (prop.Kind is not Expr.ObjectPropertyKind.Value || prop.Key is null)
                throw new Exception("Invalid assignment target.");  // getters/setters/methods aren't patterns

            Expr access = MakePropertyAccess(src, prop.Key, usedKeys);
            LowerElementWithDefault(prop.Value, access, stmts, line);
        }
    }

    // Lowers one pattern element / property value to an assignment, peeling off a default (`= expr`)
    // that the eager parser captured as an Assign (variable target) or Set/SetIndex/SetPrivate (member
    // target). `?? default` applies the default when the read is null/undefined (matching the existing
    // declaration desugaring; ECMA-262 is undefined-only — tracked separately).
    private void LowerElementWithDefault(Expr element, Expr access, List<Stmt> stmts, int line)
    {
        switch (Unwrap(element))
        {
            case Expr.Assign def:
                LowerAssignmentTarget(new Expr.Variable(def.Name),
                    new Expr.NullishCoalescing(access, def.Value), stmts, line);
                break;
            case Expr.Set def:
                LowerAssignmentTarget(new Expr.Get(def.Object, def.Name),
                    new Expr.NullishCoalescing(access, def.Value), stmts, line);
                break;
            case Expr.SetIndex def:
                LowerAssignmentTarget(new Expr.GetIndex(def.Object, def.Index),
                    new Expr.NullishCoalescing(access, def.Value), stmts, line);
                break;
            case Expr.SetPrivate def:
                LowerAssignmentTarget(new Expr.GetPrivate(def.Object, def.Name),
                    new Expr.NullishCoalescing(access, def.Value), stmts, line);
                break;
            case Expr.DestructuringAssign:
                // A nested pattern WITH a default (`[[a] = []]`, `{p: {x} = {}}`) was eagerly lowered,
                // losing its raw target; recovering it needs cover-grammar reparsing. Rare — see #779.
                throw new Exception("Nested destructuring with a default is not supported in assignment destructuring.");
            default:
                LowerAssignmentTarget(element, access, stmts, line);
                break;
        }
    }

    private static Expr MakeAssignmentExpr(Expr target, Expr value) => Unwrap(target) switch
    {
        Expr.Variable v => new Expr.Assign(v.Name, value),
        Expr.Get g => new Expr.Set(g.Object, g.Name, value),
        Expr.GetIndex gi => new Expr.SetIndex(gi.Object, gi.Index, value),
        Expr.GetPrivate gp => new Expr.SetPrivate(gp.Object, gp.Name, value),
        _ => throw new Exception("Invalid assignment target.")
    };

    private Expr MakePropertyAccess(Expr src, Expr.PropertyKey key, List<Expr> usedKeys)
    {
        switch (key)
        {
            case Expr.IdentifierKey ik:
                usedKeys.Add(new Expr.Literal(ik.Name.Lexeme));
                return new Expr.Get(src, ik.Name);
            case Expr.LiteralKey lk:
                string name = lk.Literal.Type == TokenType.STRING
                    ? (string)lk.Literal.Literal!
                    : lk.Literal.Literal!.ToString()!;
                usedKeys.Add(new Expr.Literal(name));
                return new Expr.GetIndex(src, new Expr.Literal(name));
            case Expr.ComputedKey ck:
                // A computed key is dynamic, so it cannot be added to the static rest-exclusion list
                // (a `{[k]: v, ...rest}` pattern would over-include — a narrow, rare edge).
                return new Expr.GetIndex(src, ck.Expression);
            default:
                throw new Exception("Invalid assignment target.");
        }
    }

    private static Expr MakeArrayDestructureCall(Expr source, int line) => new Expr.Call(
        new Expr.Variable(new Token(TokenType.IDENTIFIER, "__arrayDestructure", null, line)),
        new Token(TokenType.RIGHT_PAREN, ")", null, line), null, [source]);

    private static Expr MakeSliceCall(Expr src, int index, int line) => new Expr.Call(
        new Expr.Get(src, new Token(TokenType.IDENTIFIER, "slice", null, line)),
        new Token(TokenType.RIGHT_PAREN, ")", null, line), null, [new Expr.Literal((double)index)]);

    private static Expr MakeObjectRestCall(Expr src, List<Expr> usedKeys, int line) => new Expr.Call(
        new Expr.Variable(new Token(TokenType.IDENTIFIER, "__objectRest", null, line)),
        new Token(TokenType.RIGHT_PAREN, ")", null, line), null,
        [src, new Expr.ArrayLiteral(usedKeys)]);
}
