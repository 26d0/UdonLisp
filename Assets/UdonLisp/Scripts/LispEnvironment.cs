using VRC.SDK3.Data;

namespace UdonLisp
{
    /// <summary>
    /// Lexical environment (scope) for the Lisp interpreter.
    /// 
    /// Encoding: DataDictionary where:
    ///   "__parent" -> DataToken wrapping parent DataDictionary (or DataToken with null)
    ///   all other keys -> binding name (string) -> LispValue (DataList wrapped in DataToken)
    ///
    /// All methods are static.
    /// </summary>
    public static class LispEnv
    {
        private const string ParentKey = "__parent__";

        public static DataDictionary Create(DataDictionary parent)
        {
            var env = new DataDictionary();
            if (parent != null)
                env.SetValue(ParentKey, new DataToken(parent));
            return env;
        }

        public static void Define(DataDictionary env, string name, DataList value)
        {
            env.SetValue(name, new DataToken(value));
        }

        [UdonSharp.RecursiveMethod]
        public static DataList Lookup(DataDictionary env, string name)
        {
            DataToken val;
            if (env.TryGetValue(name, out val))
                return val.DataList;

            // Walk parent chain
            DataToken parentTok;
            if (env.TryGetValue(ParentKey, out parentTok))
            {
                if (parentTok.TokenType == TokenType.DataDictionary)
                    return Lookup(parentTok.DataDictionary, name);
            }
            return null; // not found
        }

        [UdonSharp.RecursiveMethod]
        public static bool Set(DataDictionary env, string name, DataList value)
        {
            if (env.ContainsKey(name) && name != ParentKey)
            {
                env.SetValue(name, new DataToken(value));
                return true;
            }

            DataToken parentTok;
            if (env.TryGetValue(ParentKey, out parentTok))
            {
                if (parentTok.TokenType == TokenType.DataDictionary)
                    return Set(parentTok.DataDictionary, name, value);
            }
            return false;
        }

        /// <summary>
        /// Create the global environment with built-in function bindings.
        /// </summary>
        public static DataDictionary CreateGlobal()
        {
            var env = Create(null);

            // Constants
            Define(env, "nil", Lv.Nil());

            // Arithmetic
            Define(env, "+", Lv.Builtin("+"));
            Define(env, "-", Lv.Builtin("-"));
            Define(env, "*", Lv.Builtin("*"));
            Define(env, "/", Lv.Builtin("/"));
            Define(env, "mod", Lv.Builtin("mod"));

            // Comparison
            Define(env, "=", Lv.Builtin("="));
            Define(env, "<", Lv.Builtin("<"));
            Define(env, ">", Lv.Builtin(">"));
            Define(env, "<=", Lv.Builtin("<="));
            Define(env, ">=", Lv.Builtin(">="));

            // Logic
            Define(env, "not", Lv.Builtin("not"));

            // List operations
            Define(env, "list", Lv.Builtin("list"));
            Define(env, "car", Lv.Builtin("car"));
            Define(env, "cdr", Lv.Builtin("cdr"));
            Define(env, "cons", Lv.Builtin("cons"));
            Define(env, "length", Lv.Builtin("length"));
            Define(env, "null?", Lv.Builtin("null?"));
            Define(env, "pair?", Lv.Builtin("pair?"));
            Define(env, "set-car!", Lv.Builtin("set-car!"));
            Define(env, "set-cdr!", Lv.Builtin("set-cdr!"));

            // Type predicates
            Define(env, "number?", Lv.Builtin("number?"));
            Define(env, "string?", Lv.Builtin("string?"));
            Define(env, "symbol?", Lv.Builtin("symbol?"));
            Define(env, "list?", Lv.Builtin("list?"));
            Define(env, "bool?", Lv.Builtin("bool?"));
            Define(env, "procedure?", Lv.Builtin("procedure?"));

            // String operations
            Define(env, "string-append", Lv.Builtin("string-append"));
            Define(env, "string-length", Lv.Builtin("string-length"));
            Define(env, "number->string", Lv.Builtin("number->string"));
            Define(env, "string->number", Lv.Builtin("string->number"));

            // I/O
            Define(env, "display", Lv.Builtin("display"));
            Define(env, "write", Lv.Builtin("write"));
            Define(env, "newline", Lv.Builtin("newline"));
            Define(env, "symbol->string", Lv.Builtin("symbol->string"));
            Define(env, "string->symbol", Lv.Builtin("string->symbol"));

            // Higher-order
            Define(env, "apply", Lv.Builtin("apply"));
            Define(env, "map", Lv.Builtin("map"));
            Define(env, "for-each", Lv.Builtin("for-each"));

            // Equivalence
            Define(env, "eq?", Lv.Builtin("eq?"));
            Define(env, "eqv?", Lv.Builtin("eqv?"));
            Define(env, "equal?", Lv.Builtin("equal?"));

            // List library (Phase 1.6)
            Define(env, "append", Lv.Builtin("append"));
            Define(env, "reverse", Lv.Builtin("reverse"));
            Define(env, "list-tail", Lv.Builtin("list-tail"));
            Define(env, "list-ref", Lv.Builtin("list-ref"));
            Define(env, "memq", Lv.Builtin("memq"));
            Define(env, "memv", Lv.Builtin("memv"));
            Define(env, "member", Lv.Builtin("member"));
            Define(env, "assq", Lv.Builtin("assq"));
            Define(env, "assv", Lv.Builtin("assv"));
            Define(env, "assoc", Lv.Builtin("assoc"));

            // cXXXr compositions (2-level)
            Define(env, "caar", Lv.Builtin("caar"));
            Define(env, "cadr", Lv.Builtin("cadr"));
            Define(env, "cdar", Lv.Builtin("cdar"));
            Define(env, "cddr", Lv.Builtin("cddr"));
            // cXXXr compositions (3-level)
            Define(env, "caaar", Lv.Builtin("caaar"));
            Define(env, "caadr", Lv.Builtin("caadr"));
            Define(env, "cadar", Lv.Builtin("cadar"));
            Define(env, "caddr", Lv.Builtin("caddr"));
            Define(env, "cdaar", Lv.Builtin("cdaar"));
            Define(env, "cdadr", Lv.Builtin("cdadr"));
            Define(env, "cddar", Lv.Builtin("cddar"));
            Define(env, "cdddr", Lv.Builtin("cdddr"));
            // cXXXr compositions (4-level)
            Define(env, "caaaar", Lv.Builtin("caaaar"));
            Define(env, "caaadr", Lv.Builtin("caaadr"));
            Define(env, "caadar", Lv.Builtin("caadar"));
            Define(env, "caaddr", Lv.Builtin("caaddr"));
            Define(env, "cadaar", Lv.Builtin("cadaar"));
            Define(env, "cadadr", Lv.Builtin("cadadr"));
            Define(env, "caddar", Lv.Builtin("caddar"));
            Define(env, "cadddr", Lv.Builtin("cadddr"));
            Define(env, "cdaaar", Lv.Builtin("cdaaar"));
            Define(env, "cdaadr", Lv.Builtin("cdaadr"));
            Define(env, "cdadar", Lv.Builtin("cdadar"));
            Define(env, "cdaddr", Lv.Builtin("cdaddr"));
            Define(env, "cddaar", Lv.Builtin("cddaar"));
            Define(env, "cddadr", Lv.Builtin("cddadr"));
            Define(env, "cdddar", Lv.Builtin("cdddar"));
            Define(env, "cddddr", Lv.Builtin("cddddr"));

            // Numeric library (Phase 1.9)
            // Predicates
            Define(env, "integer?", Lv.Builtin("integer?"));
            Define(env, "zero?", Lv.Builtin("zero?"));
            Define(env, "positive?", Lv.Builtin("positive?"));
            Define(env, "negative?", Lv.Builtin("negative?"));
            Define(env, "odd?", Lv.Builtin("odd?"));
            Define(env, "even?", Lv.Builtin("even?"));
            Define(env, "exact?", Lv.Builtin("exact?"));
            Define(env, "inexact?", Lv.Builtin("inexact?"));
            // Arithmetic
            Define(env, "abs", Lv.Builtin("abs"));
            Define(env, "max", Lv.Builtin("max"));
            Define(env, "min", Lv.Builtin("min"));
            Define(env, "quotient", Lv.Builtin("quotient"));
            Define(env, "remainder", Lv.Builtin("remainder"));
            Define(env, "modulo", Lv.Builtin("modulo"));
            Define(env, "gcd", Lv.Builtin("gcd"));
            Define(env, "lcm", Lv.Builtin("lcm"));
            // Math
            Define(env, "floor", Lv.Builtin("floor"));
            Define(env, "ceiling", Lv.Builtin("ceiling"));
            Define(env, "truncate", Lv.Builtin("truncate"));
            Define(env, "round", Lv.Builtin("round"));
            Define(env, "sqrt", Lv.Builtin("sqrt"));
            Define(env, "expt", Lv.Builtin("expt"));
            Define(env, "exp", Lv.Builtin("exp"));
            Define(env, "log", Lv.Builtin("log"));
            Define(env, "sin", Lv.Builtin("sin"));
            Define(env, "cos", Lv.Builtin("cos"));
            Define(env, "tan", Lv.Builtin("tan"));
            Define(env, "asin", Lv.Builtin("asin"));
            Define(env, "acos", Lv.Builtin("acos"));
            Define(env, "atan", Lv.Builtin("atan"));
            // Conversion
            Define(env, "exact->inexact", Lv.Builtin("exact->inexact"));
            Define(env, "inexact->exact", Lv.Builtin("inexact->exact"));

            // Character procedures (Phase 2.1)
            Define(env, "char?", Lv.Builtin("char?"));
            Define(env, "char=?", Lv.Builtin("char=?"));
            Define(env, "char<?", Lv.Builtin("char<?"));
            Define(env, "char>?", Lv.Builtin("char>?"));
            Define(env, "char<=?", Lv.Builtin("char<=?"));
            Define(env, "char>=?", Lv.Builtin("char>=?"));
            Define(env, "char-ci=?", Lv.Builtin("char-ci=?"));
            Define(env, "char-ci<?", Lv.Builtin("char-ci<?"));
            Define(env, "char-ci>?", Lv.Builtin("char-ci>?"));
            Define(env, "char-ci<=?", Lv.Builtin("char-ci<=?"));
            Define(env, "char-ci>=?", Lv.Builtin("char-ci>=?"));
            Define(env, "char-alphabetic?", Lv.Builtin("char-alphabetic?"));
            Define(env, "char-numeric?", Lv.Builtin("char-numeric?"));
            Define(env, "char-whitespace?", Lv.Builtin("char-whitespace?"));
            Define(env, "char-upcase", Lv.Builtin("char-upcase"));
            Define(env, "char-downcase", Lv.Builtin("char-downcase"));
            Define(env, "char->integer", Lv.Builtin("char->integer"));
            Define(env, "integer->char", Lv.Builtin("integer->char"));

            // String expansion (Phase 2.2)
            Define(env, "make-string", Lv.Builtin("make-string"));
            Define(env, "string", Lv.Builtin("string"));
            Define(env, "string-ref", Lv.Builtin("string-ref"));
            Define(env, "string-set!", Lv.Builtin("string-set!"));
            Define(env, "string=?", Lv.Builtin("string=?"));
            Define(env, "string<?", Lv.Builtin("string<?"));
            Define(env, "string>?", Lv.Builtin("string>?"));
            Define(env, "string<=?", Lv.Builtin("string<=?"));
            Define(env, "string>=?", Lv.Builtin("string>=?"));
            Define(env, "string-ci=?", Lv.Builtin("string-ci=?"));
            Define(env, "string-ci<?", Lv.Builtin("string-ci<?"));
            Define(env, "string-ci>?", Lv.Builtin("string-ci>?"));
            Define(env, "string-ci<=?", Lv.Builtin("string-ci<=?"));
            Define(env, "string-ci>=?", Lv.Builtin("string-ci>=?"));
            Define(env, "substring", Lv.Builtin("substring"));
            Define(env, "string-copy", Lv.Builtin("string-copy"));
            Define(env, "string->list", Lv.Builtin("string->list"));
            Define(env, "list->string", Lv.Builtin("list->string"));

            // Vector procedures (Phase 2.3)
            Define(env, "vector", Lv.Builtin("vector"));
            Define(env, "make-vector", Lv.Builtin("make-vector"));
            Define(env, "vector?", Lv.Builtin("vector?"));
            Define(env, "vector-ref", Lv.Builtin("vector-ref"));
            Define(env, "vector-set!", Lv.Builtin("vector-set!"));
            Define(env, "vector-length", Lv.Builtin("vector-length"));
            Define(env, "vector->list", Lv.Builtin("vector->list"));
            Define(env, "list->vector", Lv.Builtin("list->vector"));
            Define(env, "vector-fill!", Lv.Builtin("vector-fill!"));

            // eval and environment specifiers (Phase 2.5)
            Define(env, "eval", Lv.Builtin("eval"));
            Define(env, "scheme-report-environment", Lv.Builtin("scheme-report-environment"));
            Define(env, "interaction-environment", Lv.Builtin("interaction-environment"));

            // Multiple return values (Phase 2.8)
            Define(env, "values", Lv.Builtin("values"));
            Define(env, "call-with-values", Lv.Builtin("call-with-values"));

            // Promises (Phase 3.2) — delay is a special form, not registered here
            Define(env, "force", Lv.Builtin("force"));
            Define(env, "promise?", Lv.Builtin("promise?"));
            Define(env, "make-promise", Lv.Builtin("make-promise"));

            // Continuations (Phase 3.3) — escape-only call/cc
            Define(env, "call-with-current-continuation", Lv.Builtin("call-with-current-continuation"));
            Define(env, "call/cc", Lv.Builtin("call/cc"));

            // Dynamic wind (Phase 3.4)
            Define(env, "dynamic-wind", Lv.Builtin("dynamic-wind"));

            // Reader (Phase 4.3)
            Define(env, "read", Lv.Builtin("read"));
            Define(env, "eof-object?", Lv.Builtin("eof-object?"));

            // Picture language (SICP 2.2.4) — vector ops
            Define(env, "make-vect", Lv.Builtin("make-vect"));
            Define(env, "xcor-vect", Lv.Builtin("xcor-vect"));
            Define(env, "ycor-vect", Lv.Builtin("ycor-vect"));
            Define(env, "add-vect", Lv.Builtin("add-vect"));
            Define(env, "sub-vect", Lv.Builtin("sub-vect"));
            Define(env, "scale-vect", Lv.Builtin("scale-vect"));
            // Picture language — frame ops
            Define(env, "make-frame", Lv.Builtin("make-frame"));
            Define(env, "origin-frame", Lv.Builtin("origin-frame"));
            Define(env, "edge1-frame", Lv.Builtin("edge1-frame"));
            Define(env, "edge2-frame", Lv.Builtin("edge2-frame"));
            // Picture language — segment ops
            Define(env, "make-segment", Lv.Builtin("make-segment"));
            Define(env, "start-segment", Lv.Builtin("start-segment"));
            Define(env, "end-segment", Lv.Builtin("end-segment"));
            // Picture language — canvas/rendering
            Define(env, "draw-line", Lv.Builtin("draw-line"));
            Define(env, "image-painter", Lv.Builtin("image-painter"));
            Define(env, "image-painter-idx", Lv.Builtin("image-painter-idx"));
            Define(env, "render-painter", Lv.Builtin("render-painter"));

            return env;
        }
    }
}
