using UdonSharp;
using UnityEngine;
using VRC.SDK3.Data;

namespace UdonLisp
{
    /// <summary>
    /// Evaluator for the Lisp AST (DataList-based).
    /// All methods are static.
    /// Supports both legacy TList and TPair representations for code and data.
    /// </summary>
    public static class LispEval
    {
        // ---- Tail-call optimization ----
        // Registers: set by TailCall/TailCallFunction, read by Eval trampoline.
        // Allocated once per Eval entry and passed through via tc[] array.
        // tc[0] = next expr (DataList), tc[1] = sentinel (non-null = tail call requested)
        // tcEnv[0] = next env (DataDictionary)

        /// <summary>
        /// Signal a tail call by writing into the register arrays.
        /// Returns null as the sentinel — the trampoline checks for null.
        /// </summary>
        private static DataList SetTailCall(DataList[] tc, DataDictionary[] tcEnv,
            DataList nextExpr, DataDictionary nextEnvVal)
        {
            tc[0] = nextExpr;
            tcEnv[0] = nextEnvVal;
            return null;
        }

        [RecursiveMethod]
        public static DataList Eval(DataList expr, DataDictionary env, LispRunner runner = null)
        {
            // Allocate trampoline registers once per top-level Eval call
            var tc = new DataList[1];
            var tcEnv = new DataDictionary[1];

            while (true)
            {
                if (expr == null) return Lv.Nil();

                string tag = Lv.Tag(expr);

                DataList result;

                switch (tag)
                {
                    // Self-evaluating
                    case Lv.TNil:
                    case Lv.TBool:
                    case Lv.TInt:
                    case Lv.TFloat:
                    case Lv.TStr:
                    case Lv.TErr:
                    case Lv.TPromise:
                    case Lv.TEscape:
                    case Lv.TEof:
                        return expr;

                    // Symbol lookup
                    case Lv.TSym:
                        var val = LispEnv.Lookup(env, Lv.GetSym(expr));
                        if (val == null)
                            return Lv.Err("undefined symbol: " + Lv.GetSym(expr));
                        return val;

                    // List or Pair (function call or special form)
                    case Lv.TList:
                    case Lv.TPair:
                        result = EvalList(expr, env, tc, tcEnv, runner);
                        break;

                    default:
                        return expr;
                }

                // Trampoline: if result is null, a tail call was requested
                if (result == null)
                {
                    expr = tc[0];
                    env = tcEnv[0];
                    continue;
                }
                return result;
            }
        }

        [RecursiveMethod]
        private static DataList EvalList(DataList expr, DataDictionary env,
            DataList[] tc, DataDictionary[] tcEnv, LispRunner runner = null)
        {
            int count = Lv.Count(expr);
            if (count == 0) return Lv.Nil();

            var head = Lv.GetAt(expr, 0);

            // Special forms
            if (Lv.Tag(head) == Lv.TSym)
            {
                string sym = Lv.GetSym(head);
                switch (sym)
                {
                    case "quote": return EvalQuote(expr);
                    case "if": return EvalIf(expr, env, tc, tcEnv, runner);
                    case "cond": return EvalCond(expr, env, tc, tcEnv, runner);
                    case "define": return EvalDefine(expr, env, runner);
                    case "set!": return EvalSetBang(expr, env, runner);
                    case "lambda": return EvalLambda(expr, env);
                    case "begin": return EvalBegin(expr, env, tc, tcEnv, runner);
                    case "let": return EvalLet(expr, env, tc, tcEnv, runner);
                    case "let*": return EvalLetStar(expr, env, tc, tcEnv, runner);
                    case "letrec": return EvalLetrec(expr, env, tc, tcEnv, runner);
                    case "and": return EvalAnd(expr, env, tc, tcEnv, runner);
                    case "or": return EvalOr(expr, env, tc, tcEnv, runner);
                    case "when": return EvalWhen(expr, env, tc, tcEnv, runner);
                    case "unless": return EvalUnless(expr, env, tc, tcEnv, runner);
                    case "case": return EvalCase(expr, env, tc, tcEnv, runner);
                    case "do": return EvalDo(expr, env, tc, tcEnv, runner);
                    case "quasiquote": return EvalQuasiquote(expr, env, runner);
                    case "delay": return EvalDelay(expr, env);
                }
            }

            // Function call
            var func = Eval(head, env, runner);
            if (Lv.ShouldReturn(func)) return func;

            // Evaluate arguments
            var args = new DataList[count - 1];
            for (int i = 1; i < count; i++)
            {
                args[i - 1] = Eval(Lv.GetAt(expr, i), env, runner);
                if (Lv.ShouldReturn(args[i - 1])) return args[i - 1];
            }

            string funcTag = Lv.Tag(func);
            if (funcTag == Lv.TBuiltin)
                return CallBuiltin(Lv.GetBuiltinName(func), args, runner);
            if (funcTag == Lv.TFn)
                return TailCallFunction(func, args, tc, tcEnv);
            if (funcTag == Lv.TCont)
            {
                // Invoking an escape continuation: create escape signal
                DataList val = args.Length > 0 ? args[0] : Lv.Nil();
                return Lv.Escape(Lv.ContSentinel(func), val);
            }

            return Lv.Err("not a function: " + LispPrinter.Print(func));
        }

        // ---- Helpers ----

        /// <summary>
        /// Wrap multiple body expressions into a single expression.
        /// If there is exactly one body, return it as-is.
        /// If there are multiple bodies, wrap them in (begin body0 body1 ...).
        /// Works with both TList and TPair AST nodes.
        ///
        /// R5RS §5.2.2: Internal definitions at the start of a body are
        /// transformed into an equivalent letrec. For example:
        ///   (lambda () (define x 1) (define y 2) (+ x y))
        /// becomes:
        ///   (lambda () (letrec ((x 1) (y 2)) (+ x y)))
        /// </summary>
        private static DataList WrapImplicitBegin(DataList expr, int bodyStartIndex)
        {
            int count = Lv.Count(expr);
            int bodyCount = count - bodyStartIndex;
            if (bodyCount == 0)
                return Lv.Nil();

            // --- Internal definitions (R5RS §5.2.2) ---
            // Scan leading define forms and collect them as letrec bindings.
            int defCount = 0;
            for (int i = bodyStartIndex; i < count; i++)
            {
                DataList form = Lv.GetAt(expr, i);
                if (!IsDefineForm(form)) break;
                defCount++;
            }

            if (defCount > 0)
            {
                int remainBody = bodyCount - defCount;
                if (remainBody <= 0)
                    return Lv.Err("body must contain at least one expression after internal defines");

                // Build letrec bindings from the define forms
                var bindings = new DataList[defCount];
                for (int i = 0; i < defCount; i++)
                {
                    DataList def = Lv.GetAt(expr, bodyStartIndex + i);
                    bindings[i] = DefineToBinding(def);
                }
                var bindingsList = Lv.PairList(bindings);

                // Build (letrec (bindings...) remainBody...)
                // letrec + bindings + remaining body expressions
                var letrecItems = new DataList[2 + remainBody];
                letrecItems[0] = Lv.Sym("letrec");
                letrecItems[1] = bindingsList;
                for (int i = 0; i < remainBody; i++)
                    letrecItems[2 + i] = Lv.GetAt(expr, bodyStartIndex + defCount + i);
                return Lv.PairList(letrecItems);
            }

            // --- Normal case (no internal defines) ---
            if (bodyCount == 1)
                return Lv.GetAt(expr, bodyStartIndex);

            // Build (begin body0 body1 ...) as a pair chain
            var items = new DataList[bodyCount + 1];
            items[0] = Lv.Sym("begin");
            for (int i = 0; i < bodyCount; i++)
                items[i + 1] = Lv.GetAt(expr, bodyStartIndex + i);
            return Lv.PairList(items);
        }

        /// <summary>
        /// Check if a form is a (define ...) expression.
        /// </summary>
        private static bool IsDefineForm(DataList form)
        {
            string tag = Lv.Tag(form);
            if (tag != Lv.TList && tag != Lv.TPair) return false;
            if (Lv.Count(form) < 3) return false;
            DataList head = Lv.GetAt(form, 0);
            return Lv.Tag(head) == Lv.TSym && Lv.GetSym(head) == "define";
        }

        /// <summary>
        /// Convert a define form into a letrec binding pair (name value-expr).
        /// Handles both (define name expr) and (define (name params...) body...).
        /// Returns a pair list: (name expr) or (name (lambda (params...) body...))
        /// </summary>
        private static DataList DefineToBinding(DataList def)
        {
            DataList target = Lv.GetAt(def, 1);

            // (define name expr) → (name expr)
            if (Lv.Tag(target) == Lv.TSym)
            {
                return Lv.PairList(new DataList[] { target, Lv.GetAt(def, 2) });
            }

            // (define (name params...) body...) → (name (lambda (params...) body...))
            string tt = Lv.Tag(target);
            if ((tt == Lv.TList || tt == Lv.TPair) && Lv.Count(target) > 0)
            {
                DataList name = Lv.GetAt(target, 0);
                // Build parameter list
                int paramCount = Lv.Count(target) - 1;
                var paramItems = new DataList[paramCount];
                for (int i = 0; i < paramCount; i++)
                    paramItems[i] = Lv.GetAt(target, i + 1);
                DataList paramsList = paramCount > 0 ? Lv.PairList(paramItems) : Lv.Nil();

                // Build (lambda (params...) body...)
                int bodyCount = Lv.Count(def) - 2;
                var lambdaItems = new DataList[2 + bodyCount];
                lambdaItems[0] = Lv.Sym("lambda");
                lambdaItems[1] = paramsList;
                for (int i = 0; i < bodyCount; i++)
                    lambdaItems[2 + i] = Lv.GetAt(def, 2 + i);
                DataList lambda = Lv.PairList(lambdaItems);

                return Lv.PairList(new DataList[] { name, lambda });
            }

            // Fallback — shouldn't happen if IsDefineForm was correct
            return Lv.PairList(new DataList[] { Lv.Sym("_"), Lv.Nil() });
        }

        // ---- Special Forms ----

        private static DataList EvalQuote(DataList expr)
        {
            if (Lv.Count(expr) < 2) return Lv.Err("quote requires 1 argument");
            return Lv.GetAt(expr, 1);
        }

        /// <summary>
        /// (delay expr) — creates a promise that captures expr and the current environment.
        /// The expression is NOT evaluated until (force ...) is called.
        /// </summary>
        private static DataList EvalDelay(DataList expr, DataDictionary env)
        {
            if (Lv.Count(expr) < 2) return Lv.Err("delay: requires 1 argument");
            return Lv.Promise(Lv.GetAt(expr, 1), env);
        }

        [RecursiveMethod]
        private static DataList EvalQuasiquote(DataList expr, DataDictionary env, LispRunner runner = null)
        {
            if (Lv.Count(expr) < 2) return Lv.Err("quasiquote requires 1 argument");
            return QQExpand(Lv.GetAt(expr, 1), env, runner);
        }

        [RecursiveMethod]
        private static DataList QQExpand(DataList tmpl, DataDictionary env, LispRunner runner = null)
        {
            // If it's (unquote expr), evaluate and return
            if (IsTaggedForm(tmpl, "unquote"))
                return Eval(Lv.GetAt(tmpl, 1), env, runner);

            // If it's not a pair, return as-is (atom or nil)
            if (Lv.Tag(tmpl) != Lv.TPair)
                return tmpl;

            // It's a pair — walk the pair chain and process each element
            // Collect expanded elements into a list, handling unquote-splicing
            var items = new DataList(); // DataList of DataToken(DataList)
            DataList current = tmpl;
            while (Lv.Tag(current) == Lv.TPair)
            {
                DataList elem = Lv.Car(current);

                if (IsTaggedForm(elem, "unquote-splicing"))
                {
                    // Evaluate the splicing expression — must produce a list
                    var spliced = Eval(Lv.GetAt(elem, 1), env, runner);
                    if (Lv.ShouldReturn(spliced)) return spliced;
                    // Append each element of the spliced list
                    DataList sp = spliced;
                    while (Lv.Tag(sp) == Lv.TPair)
                    {
                        items.Add(new DataToken(Lv.Car(sp)));
                        sp = Lv.Cdr(sp);
                    }
                }
                else
                {
                    // Recursively expand this element
                    var expanded = QQExpand(elem, env, runner);
                    if (Lv.ShouldReturn(expanded)) return expanded;
                    items.Add(new DataToken(expanded));
                }

                current = Lv.Cdr(current);
            }

            // Build result as pair chain
            // If original was a dotted pair, 'current' is the non-nil cdr
            DataList tail = Lv.IsNil(current) ? Lv.Nil() : QQExpand(current, env, runner);
            if (Lv.ShouldReturn(tail)) return tail;

            for (int i = items.Count - 1; i >= 0; i--)
                tail = Lv.Pair(items[i].DataList, tail);
            return tail;
        }

        private static bool IsTaggedForm(DataList val, string tag)
        {
            int count = Lv.Count(val);
            if (count < 2) return false;
            DataList head = Lv.GetAt(val, 0);
            return Lv.Tag(head) == Lv.TSym && Lv.GetSym(head) == tag;
        }

        [RecursiveMethod]
        private static DataList EvalIf(DataList expr, DataDictionary env,
            DataList[] tc, DataDictionary[] tcEnv, LispRunner runner = null)
        {
            int count = Lv.Count(expr);
            if (count < 3) return Lv.Err("if requires at least 2 arguments");

            var cond = Eval(Lv.GetAt(expr, 1), env, runner);
            if (Lv.ShouldReturn(cond)) return cond;

            if (Lv.IsTruthy(cond))
                return SetTailCall(tc, tcEnv, Lv.GetAt(expr, 2), env);
            else if (count > 3)
                return SetTailCall(tc, tcEnv, Lv.GetAt(expr, 3), env);
            else
                return Lv.Nil();
        }

        [RecursiveMethod]
        private static DataList EvalCond(DataList expr, DataDictionary env,
            DataList[] tc, DataDictionary[] tcEnv, LispRunner runner = null)
        {
            int count = Lv.Count(expr);
            for (int i = 1; i < count; i++)
            {
                var clause = Lv.GetAt(expr, i);
                int clauseLen = Lv.Count(clause);
                if (clauseLen < 2)
                    return Lv.Err("cond clause must be (test expr)");

                var test = Lv.GetAt(clause, 0);
                if (Lv.Tag(test) == Lv.TSym && Lv.GetSym(test) == "else")
                    return SetTailCall(tc, tcEnv, Lv.GetAt(clause, 1), env);

                var result = Eval(test, env, runner);
                if (Lv.ShouldReturn(result)) return result;
                if (Lv.IsTruthy(result))
                    return SetTailCall(tc, tcEnv, Lv.GetAt(clause, 1), env);
            }
            return Lv.Nil();
        }

        [RecursiveMethod]
        private static DataList EvalCase(DataList expr, DataDictionary env,
            DataList[] tc, DataDictionary[] tcEnv, LispRunner runner = null)
        {
            // (case <key> ((<datum> ...) <body> ...) ... (else <body> ...))
            int count = Lv.Count(expr);
            if (count < 3) return Lv.Err("case: requires key and at least one clause");

            var key = Eval(Lv.GetAt(expr, 1), env, runner);
            if (Lv.ShouldReturn(key)) return key;

            for (int i = 2; i < count; i++)
            {
                var clause = Lv.GetAt(expr, i);
                int clauseLen = Lv.Count(clause);
                if (clauseLen < 2)
                    return Lv.Err("case: clause must have datums and body");

                var datums = Lv.GetAt(clause, 0);

                // else clause
                if (Lv.Tag(datums) == Lv.TSym && Lv.GetSym(datums) == "else")
                    return EvalCaseBody(clause, env, tc, tcEnv, runner);

                // Check if key matches any datum in the list
                int datumCount = Lv.Count(datums);
                for (int j = 0; j < datumCount; j++)
                {
                    if (IsEqv(key, Lv.GetAt(datums, j)))
                        return EvalCaseBody(clause, env, tc, tcEnv, runner);
                }
            }
            return Lv.Nil();
        }

        private static DataList EvalCaseBody(DataList clause, DataDictionary env,
            DataList[] tc, DataDictionary[] tcEnv, LispRunner runner = null)
        {
            int clauseLen = Lv.Count(clause);
            if (clauseLen == 2)
                return SetTailCall(tc, tcEnv, Lv.GetAt(clause, 1), env);
            // implicit begin for multiple body expressions
            var items = new DataList[clauseLen];
            items[0] = Lv.Sym("begin");
            for (int k = 1; k < clauseLen; k++)
                items[k] = Lv.GetAt(clause, k);
            return SetTailCall(tc, tcEnv, Lv.PairList(items), env);
        }

        [RecursiveMethod]
        private static DataList EvalDo(DataList expr, DataDictionary env,
            DataList[] tc, DataDictionary[] tcEnv, LispRunner runner = null)
        {
            // (do ((<var> <init> <step>) ...) (<test> <expr> ...) <command> ...)
            int count = Lv.Count(expr);
            if (count < 3) return Lv.Err("do: requires bindings and test clause");

            var bindings = Lv.GetAt(expr, 1);
            var testClause = Lv.GetAt(expr, 2);
            int bindCount = Lv.Count(bindings);
            int testLen = Lv.Count(testClause);
            if (testLen < 1) return Lv.Err("do: test clause must have a test");

            // Collect variable names, init values, step expressions
            var varNames = new string[bindCount];
            var stepExprs = new DataList[bindCount];
            bool[] hasStep = new bool[bindCount];

            // Create do environment and initialize variables
            var doEnv = LispEnv.Create(env);
            for (int i = 0; i < bindCount; i++)
            {
                var binding = Lv.GetAt(bindings, i);
                int bLen = Lv.Count(binding);
                if (bLen < 2) return Lv.Err("do: binding must have variable and init");
                varNames[i] = Lv.GetSym(Lv.GetAt(binding, 0));
                var initVal = Eval(Lv.GetAt(binding, 1), env, runner);
                if (Lv.ShouldReturn(initVal)) return initVal;
                LispEnv.Define(doEnv, varNames[i], initVal);
                if (bLen >= 3)
                {
                    stepExprs[i] = Lv.GetAt(binding, 2);
                    hasStep[i] = true;
                }
            }

            // Iteration loop
            for (int iter = 0; iter < 100000; iter++)
            {
                // Evaluate test
                var testResult = Eval(Lv.GetAt(testClause, 0), doEnv, runner);
                if (Lv.ShouldReturn(testResult)) return testResult;

                if (Lv.IsTruthy(testResult))
                {
                    // Test is true — evaluate result expressions
                    if (testLen == 1) return Lv.Nil();
                    DataList result = Lv.Nil();
                    for (int j = 1; j < testLen - 1; j++)
                    {
                        result = Eval(Lv.GetAt(testClause, j), doEnv, runner);
                        if (Lv.ShouldReturn(result)) return result;
                    }
                    // Last result expression is in tail position
                    return SetTailCall(tc, tcEnv, Lv.GetAt(testClause, testLen - 1), doEnv);
                }

                // Evaluate commands (side effects)
                for (int j = 3; j < count; j++)
                {
                    var cmd = Eval(Lv.GetAt(expr, j), doEnv, runner);
                    if (Lv.ShouldReturn(cmd)) return cmd;
                }

                // Evaluate all step expressions before updating
                var newVals = new DataList[bindCount];
                for (int i = 0; i < bindCount; i++)
                {
                    if (hasStep[i])
                    {
                        newVals[i] = Eval(stepExprs[i], doEnv, runner);
                        if (Lv.ShouldReturn(newVals[i])) return newVals[i];
                    }
                }
                // Update variables
                for (int i = 0; i < bindCount; i++)
                {
                    if (hasStep[i])
                        LispEnv.Set(doEnv, varNames[i], newVals[i]);
                }
            }
            return Lv.Err("do: iteration limit exceeded");
        }

        [RecursiveMethod]
        private static DataList EvalDefine(DataList expr, DataDictionary env, LispRunner runner = null)
        {
            int count = Lv.Count(expr);
            if (count < 3) return Lv.Err("define requires 2 arguments");

            var target = Lv.GetAt(expr, 1);

            // (define name value)
            if (Lv.Tag(target) == Lv.TSym)
            {
                var val = Eval(Lv.GetAt(expr, 2), env, runner);
                if (Lv.ShouldReturn(val)) return val;
                LispEnv.Define(env, Lv.GetSym(target), val);
                return Lv.Nil();
            }

            // (define (name params...) body...)
            string targetTag = Lv.Tag(target);
            if ((targetTag == Lv.TList || targetTag == Lv.TPair) && Lv.Count(target) > 0)
            {
                var nameVal = Lv.GetAt(target, 0);
                if (Lv.Tag(nameVal) != Lv.TSym)
                    return Lv.Err("define: first element must be a symbol");

                string funcName = Lv.GetSym(nameVal);
                int paramCount = Lv.Count(target) - 1;
                var paramNames = new DataList();
                for (int i = 0; i < paramCount; i++)
                {
                    var p = Lv.GetAt(target, i + 1);
                    if (Lv.Tag(p) != Lv.TSym)
                        return Lv.Err("define: parameter names must be symbols");
                    paramNames.Add(Lv.GetSym(p));
                }

                var body = WrapImplicitBegin(expr, 2);
                var func = Lv.Fn(paramNames, body, env);
                LispEnv.Define(env, funcName, func);
                return Lv.Nil();
            }

            return Lv.Err("define: invalid syntax");
        }

        [RecursiveMethod]
        private static DataList EvalSetBang(DataList expr, DataDictionary env, LispRunner runner = null)
        {
            if (Lv.Count(expr) < 3) return Lv.Err("set! requires 2 arguments");
            var target = Lv.GetAt(expr, 1);
            if (Lv.Tag(target) != Lv.TSym)
                return Lv.Err("set!: first argument must be a symbol");

            var val = Eval(Lv.GetAt(expr, 2), env, runner);
            if (Lv.ShouldReturn(val)) return val;

            if (!LispEnv.Set(env, Lv.GetSym(target), val))
                return Lv.Err("set!: undefined variable: " + Lv.GetSym(target));
            return Lv.Nil();
        }

        private static DataList EvalLambda(DataList expr, DataDictionary env)
        {
            if (Lv.Count(expr) < 3) return Lv.Err("lambda requires params and body");
            var paramList = Lv.GetAt(expr, 1);
            string plt = Lv.Tag(paramList);
            if (plt != Lv.TList && plt != Lv.TPair && plt != Lv.TNil)
                return Lv.Err("lambda: parameter list must be a list");

            int paramCount = Lv.Count(paramList);
            var paramNames = new DataList();
            for (int i = 0; i < paramCount; i++)
            {
                var p = Lv.GetAt(paramList, i);
                if (Lv.Tag(p) != Lv.TSym)
                    return Lv.Err("lambda: parameter names must be symbols");
                paramNames.Add(Lv.GetSym(p));
            }

            var body = WrapImplicitBegin(expr, 2);
            return Lv.Fn(paramNames, body, env);
        }

        [RecursiveMethod]
        private static DataList EvalBegin(DataList expr, DataDictionary env,
            DataList[] tc, DataDictionary[] tcEnv, LispRunner runner = null)
        {
            int count = Lv.Count(expr);
            if (count <= 1) return Lv.Nil();
            for (int i = 1; i < count - 1; i++)
            {
                var result = Eval(Lv.GetAt(expr, i), env, runner);
                if (Lv.ShouldReturn(result)) return result;
            }
            // Last expression is in tail position
            return SetTailCall(tc, tcEnv, Lv.GetAt(expr, count - 1), env);
        }

        [RecursiveMethod]
        private static DataList EvalLet(DataList expr, DataDictionary env,
            DataList[] tc, DataDictionary[] tcEnv, LispRunner runner = null)
        {
            if (Lv.Count(expr) < 3) return Lv.Err("let requires bindings and body");
            var second = Lv.GetAt(expr, 1);

            // Named let: (let name ((var init) ...) body...)
            if (Lv.Tag(second) == Lv.TSym)
                return EvalNamedLet(expr, env, tc, tcEnv, runner);

            // Regular let
            var bindings = second;
            var letEnv = LispEnv.Create(env);
            int bcount = Lv.Count(bindings);
            for (int i = 0; i < bcount; i++)
            {
                var binding = Lv.GetAt(bindings, i);
                int blen = Lv.Count(binding);
                if (blen < 2)
                    return Lv.Err("let: each binding must be (name value)");
                var nameVal = Lv.GetAt(binding, 0);
                if (Lv.Tag(nameVal) != Lv.TSym)
                    return Lv.Err("let: binding name must be a symbol");

                var val = Eval(Lv.GetAt(binding, 1), env, runner);
                if (Lv.ShouldReturn(val)) return val;
                LispEnv.Define(letEnv, Lv.GetSym(nameVal), val);
            }
            return SetTailCall(tc, tcEnv, WrapImplicitBegin(expr, 2), letEnv);
        }

        /// <summary>
        /// Named let: (let name ((var1 init1) (var2 init2) ...) body...)
        /// Desugars to: (letrec ((name (lambda (var1 var2 ...) body...))) (name init1 init2 ...))
        /// </summary>
        [RecursiveMethod]
        private static DataList EvalNamedLet(DataList expr, DataDictionary env,
            DataList[] tc, DataDictionary[] tcEnv, LispRunner runner = null)
        {
            int count = Lv.Count(expr);
            if (count < 4) return Lv.Err("named let: requires name, bindings, and body");
            string name = Lv.GetSym(Lv.GetAt(expr, 1));
            var bindings = Lv.GetAt(expr, 2);
            int bcount = Lv.Count(bindings);

            // Extract parameter names and initial values
            var paramNames = new DataList();
            var initVals = new DataList[bcount];
            for (int i = 0; i < bcount; i++)
            {
                var binding = Lv.GetAt(bindings, i);
                if (Lv.Count(binding) < 2)
                    return Lv.Err("named let: each binding must be (name value)");
                var nameVal = Lv.GetAt(binding, 0);
                if (Lv.Tag(nameVal) != Lv.TSym)
                    return Lv.Err("named let: binding name must be a symbol");
                paramNames.Add(Lv.GetSym(nameVal));
                var val = Eval(Lv.GetAt(binding, 1), env, runner);
                if (Lv.ShouldReturn(val)) return val;
                initVals[i] = val;
            }

            // Build the lambda body (implicit begin from index 3)
            var body = WrapImplicitBegin(expr, 3);

            // Create letrec-style env: bind name to the lambda, then call it
            var letEnv = LispEnv.Create(env);
            var func = Lv.Fn(paramNames, body, letEnv);
            LispEnv.Define(letEnv, name, func);

            // Call with initial values
            return TailCallFunction(func, initVals, tc, tcEnv);
        }

        /// <summary>
        /// (let* ((name1 val1) (name2 val2) ...) body...)
        /// Sequential binding: each binding sees all previous ones.
        /// </summary>
        [RecursiveMethod]
        private static DataList EvalLetStar(DataList expr, DataDictionary env,
            DataList[] tc, DataDictionary[] tcEnv, LispRunner runner = null)
        {
            if (Lv.Count(expr) < 3) return Lv.Err("let*: requires bindings and body");
            var bindings = Lv.GetAt(expr, 1);

            var letEnv = LispEnv.Create(env);
            int bcount = Lv.Count(bindings);
            for (int i = 0; i < bcount; i++)
            {
                var binding = Lv.GetAt(bindings, i);
                int blen = Lv.Count(binding);
                if (blen < 2)
                    return Lv.Err("let*: each binding must be (name value)");
                var nameVal = Lv.GetAt(binding, 0);
                if (Lv.Tag(nameVal) != Lv.TSym)
                    return Lv.Err("let*: binding name must be a symbol");

                // Evaluate in current letEnv (sees previous bindings)
                var val = Eval(Lv.GetAt(binding, 1), letEnv, runner);
                if (Lv.ShouldReturn(val)) return val;
                LispEnv.Define(letEnv, Lv.GetSym(nameVal), val);
            }
            return SetTailCall(tc, tcEnv, WrapImplicitBegin(expr, 2), letEnv);
        }

        /// <summary>
        /// (letrec ((name1 val1) (name2 val2) ...) body...)
        /// All bindings are in scope during init — needed for mutual recursion.
        /// Implementation: bind all names to nil, then evaluate and update.
        /// </summary>
        [RecursiveMethod]
        private static DataList EvalLetrec(DataList expr, DataDictionary env,
            DataList[] tc, DataDictionary[] tcEnv, LispRunner runner = null)
        {
            if (Lv.Count(expr) < 3) return Lv.Err("letrec: requires bindings and body");
            var bindings = Lv.GetAt(expr, 1);

            var letEnv = LispEnv.Create(env);
            int bcount = Lv.Count(bindings);

            // Phase 1: bind all names to nil (placeholder)
            var names = new string[bcount];
            for (int i = 0; i < bcount; i++)
            {
                var binding = Lv.GetAt(bindings, i);
                int blen = Lv.Count(binding);
                if (blen < 2)
                    return Lv.Err("letrec: each binding must be (name value)");
                var nameVal = Lv.GetAt(binding, 0);
                if (Lv.Tag(nameVal) != Lv.TSym)
                    return Lv.Err("letrec: binding name must be a symbol");
                names[i] = Lv.GetSym(nameVal);
                LispEnv.Define(letEnv, names[i], Lv.Nil());
            }

            // Phase 2: evaluate init expressions in the letrec env and update
            for (int i = 0; i < bcount; i++)
            {
                var binding = Lv.GetAt(bindings, i);
                var val = Eval(Lv.GetAt(binding, 1), letEnv, runner);
                if (Lv.ShouldReturn(val)) return val;
                LispEnv.Define(letEnv, names[i], val);
            }

            return SetTailCall(tc, tcEnv, WrapImplicitBegin(expr, 2), letEnv);
        }

        [RecursiveMethod]
        private static DataList EvalAnd(DataList expr, DataDictionary env,
            DataList[] tc, DataDictionary[] tcEnv, LispRunner runner = null)
        {
            int count = Lv.Count(expr);
            if (count <= 1) return Lv.Bool(true);
            for (int i = 1; i < count - 1; i++)
            {
                var result = Eval(Lv.GetAt(expr, i), env, runner);
                if (Lv.ShouldReturn(result)) return result;
                if (!Lv.IsTruthy(result)) return result;
            }
            // Last expression is in tail position
            return SetTailCall(tc, tcEnv, Lv.GetAt(expr, count - 1), env);
        }

        [RecursiveMethod]
        private static DataList EvalOr(DataList expr, DataDictionary env,
            DataList[] tc, DataDictionary[] tcEnv, LispRunner runner = null)
        {
            int count = Lv.Count(expr);
            if (count <= 1) return Lv.Bool(false);
            for (int i = 1; i < count - 1; i++)
            {
                var result = Eval(Lv.GetAt(expr, i), env, runner);
                if (Lv.ShouldReturn(result)) return result;
                if (Lv.IsTruthy(result)) return result;
            }
            // Last expression is in tail position
            return SetTailCall(tc, tcEnv, Lv.GetAt(expr, count - 1), env);
        }

        /// <summary>
        /// (when test expr1 expr2 ...) — if test is truthy, evaluate body
        /// expressions as an implicit begin; otherwise return void (nil).
        /// </summary>
        [RecursiveMethod]
        private static DataList EvalWhen(DataList expr, DataDictionary env,
            DataList[] tc, DataDictionary[] tcEnv, LispRunner runner = null)
        {
            int count = Lv.Count(expr);
            if (count < 3) return Lv.Err("when: requires test and at least one expression");
            var test = Eval(Lv.GetAt(expr, 1), env, runner);
            if (Lv.ShouldReturn(test)) return test;
            if (!Lv.IsTruthy(test)) return Lv.Nil();
            // Evaluate all body expressions; last is in tail position
            for (int i = 2; i < count - 1; i++)
            {
                var result = Eval(Lv.GetAt(expr, i), env, runner);
                if (Lv.ShouldReturn(result)) return result;
            }
            return SetTailCall(tc, tcEnv, Lv.GetAt(expr, count - 1), env);
        }

        /// <summary>
        /// (unless test expr1 expr2 ...) — if test is falsy, evaluate body
        /// expressions as an implicit begin; otherwise return void (nil).
        /// </summary>
        [RecursiveMethod]
        private static DataList EvalUnless(DataList expr, DataDictionary env,
            DataList[] tc, DataDictionary[] tcEnv, LispRunner runner = null)
        {
            int count = Lv.Count(expr);
            if (count < 3) return Lv.Err("unless: requires test and at least one expression");
            var test = Eval(Lv.GetAt(expr, 1), env, runner);
            if (Lv.ShouldReturn(test)) return test;
            if (Lv.IsTruthy(test)) return Lv.Nil();
            // Evaluate all body expressions; last is in tail position
            for (int i = 2; i < count - 1; i++)
            {
                var result = Eval(Lv.GetAt(expr, i), env, runner);
                if (Lv.ShouldReturn(result)) return result;
            }
            return SetTailCall(tc, tcEnv, Lv.GetAt(expr, count - 1), env);
        }

        // ---- Function Call ----

        /// <summary>
        /// Set up a function call and signal tail call to the trampoline.
        /// Used in tail position — avoids recursive Eval call.
        /// </summary>
        private static DataList TailCallFunction(DataList func, DataList[] args,
            DataList[] tc, DataDictionary[] tcEnv)
        {
            var paramNames = Lv.FnParams(func);
            if (paramNames.Count != args.Length)
                return Lv.Err("function expects " + paramNames.Count + " args, got " + args.Length);

            var funcEnv = LispEnv.Create(Lv.FnClosure(func));
            for (int i = 0; i < paramNames.Count; i++)
                LispEnv.Define(funcEnv, paramNames[i].String, args[i]);

            return SetTailCall(tc, tcEnv, Lv.FnBody(func), funcEnv);
        }

        /// <summary>
        /// Call a user function and immediately evaluate (non-tail).
        /// Used by ApplyFunction (for map, for-each, apply, call-with-values).
        /// </summary>
        [RecursiveMethod]
        private static DataList CallFunction(DataList func, DataList[] args, LispRunner runner = null)
        {
            var paramNames = Lv.FnParams(func);
            if (paramNames.Count != args.Length)
                return Lv.Err("function expects " + paramNames.Count + " args, got " + args.Length);

            var funcEnv = LispEnv.Create(Lv.FnClosure(func));
            for (int i = 0; i < paramNames.Count; i++)
                LispEnv.Define(funcEnv, paramNames[i].String, args[i]);

            return Eval(Lv.FnBody(func), funcEnv, runner);
        }

        /// <summary>
        /// Apply a function (builtin or user) to an array of arguments.
        /// Used by apply, map, for-each.
        /// </summary>
        [RecursiveMethod]
        private static DataList ApplyFunction(DataList func, DataList[] args, LispRunner runner = null)
        {
            string ft = Lv.Tag(func);
            if (ft == Lv.TBuiltin)
                return CallBuiltin(Lv.GetBuiltinName(func), args, runner);
            if (ft == Lv.TFn)
                return CallFunction(func, args, runner);
            if (ft == Lv.TCont)
            {
                DataList val = args.Length > 0 ? args[0] : Lv.Nil();
                return Lv.Escape(Lv.ContSentinel(func), val);
            }
            return Lv.Err("not a function: " + LispPrinter.Print(func));
        }

        // ---- Built-in Functions ----

        private static DataList CallBuiltin(string name, DataList[] args, LispRunner runner = null)
        {
            switch (name)
            {
                case "+": return BAdd(args);
                case "-": return BSub(args);
                case "*": return BMul(args);
                case "/": return BDiv(args);
                case "mod": return BMod(args);
                case "=": return BEq(args);
                case "<": return BLt(args);
                case ">": return BGt(args);
                case "<=": return BLe(args);
                case ">=": return BGe(args);
                case "not": return BNot(args);
                case "list": return BList(args);
                case "car": return BCar(args);
                case "cdr": return BCdr(args);
                case "cons": return BCons(args);
                case "length": return BLength(args);
                case "null?": return BNullQ(args);
                case "pair?": return BPairQ(args);
                case "number?": return BTypeQ(args, Lv.TInt, Lv.TFloat);
                case "string?": return BTypeQ(args, Lv.TStr, Lv.TStr);
                case "symbol?": return BTypeQ(args, Lv.TSym, Lv.TSym);
                case "list?": return BListQ(args);
                case "bool?": return BTypeQ(args, Lv.TBool, Lv.TBool);
                case "procedure?": return BProcedureQ(args);
                case "set-car!": return BSetCar(args);
                case "set-cdr!": return BSetCdr(args);
                case "string-append": return BStrAppend(args);
                case "string-length": return BStrLen(args);
                case "number->string": return BNumToStr(args);
                case "string->number": return BStrToNum(args);
                case "display": return BDisplay(args);
                case "write": return BWrite(args);
                case "newline": return BNewline(args);
                case "symbol->string": return BSymToStr(args);
                case "string->symbol": return BStrToSym(args);
                case "apply": return BApply(args, runner);
                case "map": return BMap(args, runner);
                case "for-each": return BForEach(args, runner);
                case "eq?": return BEqQ(args);
                case "eqv?": return BEqvQ(args);
                case "equal?": return BEqualQ(args);
                case "append": return BAppend(args);
                case "reverse": return BReverse(args);
                case "list-tail": return BListTail(args);
                case "list-ref": return BListRef(args);
                case "memq": return BMemq(args);
                case "memv": return BMemv(args);
                case "member": return BMember(args);
                case "assq": return BAssq(args);
                case "assv": return BAssv(args);
                case "assoc": return BAssoc(args);
                // Numeric predicates (Phase 1.9)
                case "integer?": return BIntegerQ(args);
                case "zero?": return BZeroQ(args);
                case "positive?": return BPositiveQ(args);
                case "negative?": return BNegativeQ(args);
                case "odd?": return BOddQ(args);
                case "even?": return BEvenQ(args);
                case "exact?": return BExactQ(args);
                case "inexact?": return BInexactQ(args);
                // Numeric arithmetic (Phase 1.9)
                case "abs": return BAbs(args);
                case "max": return BMaxMin(args, true);
                case "min": return BMaxMin(args, false);
                case "quotient": return BQuotient(args);
                case "remainder": return BRemainder(args);
                case "modulo": return BModulo(args);
                case "gcd": return BGcd(args);
                case "lcm": return BLcm(args);
                // Math functions (Phase 1.9)
                case "floor": return BMathRound(args, 0);
                case "ceiling": return BMathRound(args, 1);
                case "truncate": return BMathRound(args, 2);
                case "round": return BMathRound(args, 3);
                case "sqrt": return BMath1(args, 0);
                case "expt": return BExpt(args);
                case "exp": return BMath1(args, 1);
                case "log": return BMath1(args, 2);
                case "sin": return BMath1(args, 3);
                case "cos": return BMath1(args, 4);
                case "tan": return BMath1(args, 5);
                case "asin": return BMath1(args, 6);
                case "acos": return BMath1(args, 7);
                case "atan": return BAtan(args);
                // Conversion (Phase 1.9)
                case "exact->inexact": return BExactToInexact(args);
                case "inexact->exact": return BInexactToExact(args);
                // Character procedures (Phase 2.1)
                case "char?": return BTypeQ(args, Lv.TChar, Lv.TChar);
                case "char=?": return BCharCmp(args, 0);
                case "char<?": return BCharCmp(args, 1);
                case "char>?": return BCharCmp(args, 2);
                case "char<=?": return BCharCmp(args, 3);
                case "char>=?": return BCharCmp(args, 4);
                case "char-ci=?": return BCharCiCmp(args, 0);
                case "char-ci<?": return BCharCiCmp(args, 1);
                case "char-ci>?": return BCharCiCmp(args, 2);
                case "char-ci<=?": return BCharCiCmp(args, 3);
                case "char-ci>=?": return BCharCiCmp(args, 4);
                case "char-alphabetic?": return BCharClass(args, 0);
                case "char-numeric?": return BCharClass(args, 1);
                case "char-whitespace?": return BCharClass(args, 2);
                case "char-upcase": return BCharCase(args, true);
                case "char-downcase": return BCharCase(args, false);
                case "char->integer": return BCharToInt(args);
                case "integer->char": return BIntToChar(args);
                // String expansion (Phase 2.2)
                case "make-string": return BMakeString(args);
                case "string": return BString(args);
                case "string-ref": return BStringRef(args);
                case "string-set!": return BStringSet(args);
                case "string=?": return BStrCmp(args, 0);
                case "string<?": return BStrCmp(args, 1);
                case "string>?": return BStrCmp(args, 2);
                case "string<=?": return BStrCmp(args, 3);
                case "string>=?": return BStrCmp(args, 4);
                case "string-ci=?": return BStrCiCmp(args, 0);
                case "string-ci<?": return BStrCiCmp(args, 1);
                case "string-ci>?": return BStrCiCmp(args, 2);
                case "string-ci<=?": return BStrCiCmp(args, 3);
                case "string-ci>=?": return BStrCiCmp(args, 4);
                case "substring": return BSubstring(args);
                case "string-copy": return BStringCopy(args);
                case "string->list": return BStringToList(args);
                case "list->string": return BListToString(args);
                // Vector procedures (Phase 2.3)
                case "vector": return BVector(args);
                case "make-vector": return BMakeVector(args);
                case "vector?": return BTypeQ(args, Lv.TVec, Lv.TVec);
                case "vector-ref": return BVectorRef(args);
                case "vector-set!": return BVectorSet(args);
                case "vector-length": return BVectorLength(args);
                case "vector->list": return BVectorToList(args);
                case "list->vector": return BListToVector(args);
                case "vector-fill!": return BVectorFill(args);
                // eval and environment specifiers (Phase 2.5)
                case "eval": return BEvalProc(args, runner);
                case "scheme-report-environment": return BSchemeReportEnv(args);
                case "interaction-environment": return BInteractionEnv(args);
                // Multiple return values (Phase 2.8)
                case "values": return BValues(args);
                case "call-with-values": return BCallWithValues(args, runner);
                // Promises (Phase 3.2)
                case "force": return BForce(args, runner);
                case "promise?": return BTypeQ(args, Lv.TPromise, Lv.TPromise);
                case "make-promise": return BMakePromise(args);
                // Continuations (Phase 3.3)
                case "call-with-current-continuation": return BCallCC(args, runner);
                case "call/cc": return BCallCC(args, runner);
                // Dynamic wind (Phase 3.4)
                case "dynamic-wind": return BDynamicWind(args, runner);
                // Reader (Phase 4.3)
                case "read": return BRead(args);
                case "eof-object?": return BTypeQ(args, Lv.TEof, Lv.TEof);
                // Picture language (SICP 2.2.4) — vector ops
                case "make-vect": return BMakeVect(args);
                case "xcor-vect": return BXcorVect(args);
                case "ycor-vect": return BYcorVect(args);
                case "add-vect": return BAddVect(args);
                case "sub-vect": return BSubVect(args);
                case "scale-vect": return BScaleVect(args);
                // Picture language — frame ops
                case "make-frame": return BMakeFrame(args);
                case "origin-frame": return BOriginFrame(args);
                case "edge1-frame": return BEdge1Frame(args);
                case "edge2-frame": return BEdge2Frame(args);
                // Picture language — segment ops
                case "make-segment": return BMakeSegment(args);
                case "start-segment": return BStartSegment(args);
                case "end-segment": return BEndSegment(args);
                // Picture language — canvas/rendering
                case "draw-line": return BDrawLine(args, runner);
                case "image-painter": return BImagePainter(args, runner);
                case "image-painter-idx": return BImagePainterIdx(args, runner);
                case "render-painter": return BRenderPainter(args, runner);
                default:
                    // cXXXr dispatch: names matching c[ad]{2,4}r
                    if (name.Length >= 4 && name[0] == 'c' && name[name.Length - 1] == 'r')
                        return BCxxxr(name, args);
                    return Lv.Err("unknown builtin: " + name);
            }
        }

        // ======== eval and environment specifiers (Phase 2.5) ========

        private static DataList BEvalProc(DataList[] a, LispRunner runner = null)
        {
            if (a.Length != 2) return Lv.Err("eval: requires 2 arguments");
            DataList expr = a[0];
            if (Lv.Tag(a[1]) != Lv.TEnv) return Lv.Err("eval: second arg must be an environment");
            DataDictionary env = Lv.GetEnv(a[1]);
            return Eval(expr, env, runner);
        }

        private static DataList BSchemeReportEnv(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("scheme-report-environment: requires 1 argument");
            if (Lv.Tag(a[0]) != Lv.TInt) return Lv.Err("scheme-report-environment: expected integer");
            int ver = Lv.GetInt(a[0]);
            if (ver != 5) return Lv.Err("scheme-report-environment: only version 5 supported");
            return Lv.Env(LispEnv.CreateGlobal());
        }

        private static DataList BInteractionEnv(DataList[] a)
        {
            if (a.Length != 0) return Lv.Err("interaction-environment: takes no arguments");
            return Lv.Env(LispEnv.CreateGlobal());
        }

        // ======== Multiple return values (Phase 2.8) ========

        private static DataList BValues(DataList[] a)
        {
            if (a.Length == 1) return a[0];
            return Lv.Vals(a);
        }

        private static DataList BCallWithValues(DataList[] a, LispRunner runner = null)
        {
            if (a.Length != 2) return Lv.Err("call-with-values: requires 2 arguments");
            DataList producer = a[0];
            DataList consumer = a[1];
            // Call producer with no arguments
            DataList[] noArgs = new DataList[0];
            DataList produced = ApplyFunction(producer, noArgs, runner);
            if (Lv.Tag(produced) == Lv.TErr) return produced;
            // Unpack: if produced is a vals wrapper, expand into args; otherwise single arg
            DataList[] consArgs;
            if (Lv.Tag(produced) == Lv.TVals)
            {
                int n = Lv.ValsCount(produced);
                consArgs = new DataList[n];
                for (int i = 0; i < n; i++)
                    consArgs[i] = Lv.ValsRef(produced, i);
            }
            else
            {
                consArgs = new DataList[] { produced };
            }
            return ApplyFunction(consumer, consArgs, runner);
        }

        // ======== Promises (Phase 3.2) ========

        [RecursiveMethod]
        private static DataList BForce(DataList[] a, LispRunner runner = null)
        {
            if (a.Length != 1) return Lv.Err("force: requires 1 argument");
            DataList p = a[0];
            if (Lv.Tag(p) != Lv.TPromise)
            {
                // R5RS: (force x) where x is not a promise returns x
                return p;
            }
            if (Lv.IsPromiseForced(p))
                return Lv.PromiseResult(p);
            // Evaluate the stored expression in the stored environment
            DataList result = Eval(Lv.PromiseExpr(p), Lv.PromiseEnv(p), runner);
            if (Lv.ShouldReturn(result)) return result;
            // If the result is itself a promise (iterative forcing), force it
            // This handles (delay (delay x)) correctly
            if (Lv.Tag(result) == Lv.TPromise)
            {
                DataList[] forceArgs = new DataList[] { result };
                result = BForce(forceArgs, runner);
                if (Lv.ShouldReturn(result)) return result;
            }
            // Cache the result (memoization)
            Lv.PromiseSetForced(p, result);
            return result;
        }

        private static DataList BMakePromise(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("make-promise: requires 1 argument");
            // If already a promise, return it as-is
            if (Lv.Tag(a[0]) == Lv.TPromise) return a[0];
            // Wrap a computed value in an already-forced promise
            return Lv.PromiseForced(a[0]);
        }

        // ======== Continuations (Phase 3.3) ========

        /// <summary>
        /// (call-with-current-continuation proc) / (call/cc proc)
        /// Escape-only implementation: the continuation can only be used
        /// during the dynamic extent of the call/cc invocation.
        /// </summary>
        [RecursiveMethod]
        private static DataList BCallCC(DataList[] a, LispRunner runner = null)
        {
            if (a.Length != 1) return Lv.Err("call/cc: requires 1 argument");
            DataList proc = a[0];
            string pt = Lv.Tag(proc);
            if (pt != Lv.TFn && pt != Lv.TBuiltin && pt != Lv.TCont)
                return Lv.Err("call/cc: argument must be a procedure");
            // Create a unique sentinel for this call/cc invocation
            var sentinel = new DataList();
            sentinel.Add("__cc_sentinel__");
            // Create the continuation object
            DataList cont = Lv.Cont(sentinel);
            // Call the procedure with the continuation as its argument
            DataList[] callArgs = new DataList[] { cont };
            DataList result = ApplyFunction(proc, callArgs, runner);
            // If the result is an escape matching our sentinel, unwrap it
            if (Lv.IsEscape(result) && Lv.EscapeSentinel(result) == sentinel)
                return Lv.EscapeValue(result);
            // Otherwise return the result as-is (normal return or unrelated escape)
            return result;
        }

        /// <summary>
        /// (dynamic-wind before thunk after)
        /// Calls before (zero args), then thunk (zero args), then after (zero args).
        /// Returns the result of thunk. If thunk escapes via call/cc, after is
        /// still called before the escape propagates.
        /// </summary>
        [RecursiveMethod]
        private static DataList BDynamicWind(DataList[] a, LispRunner runner = null)
        {
            if (a.Length != 3) return Lv.Err("dynamic-wind: requires 3 arguments");
            DataList before = a[0];
            DataList thunk = a[1];
            DataList after = a[2];
            // Validate all three are procedures
            string bt = Lv.Tag(before);
            if (bt != Lv.TFn && bt != Lv.TBuiltin && bt != Lv.TCont)
                return Lv.Err("dynamic-wind: before must be a procedure");
            string tt = Lv.Tag(thunk);
            if (tt != Lv.TFn && tt != Lv.TBuiltin && tt != Lv.TCont)
                return Lv.Err("dynamic-wind: thunk must be a procedure");
            string at = Lv.Tag(after);
            if (at != Lv.TFn && at != Lv.TBuiltin && at != Lv.TCont)
                return Lv.Err("dynamic-wind: after must be a procedure");
            DataList[] noArgs = new DataList[0];
            // Call before thunk
            DataList bResult = ApplyFunction(before, noArgs, runner);
            if (Lv.ShouldReturn(bResult)) return bResult;
            // Call thunk
            DataList result = ApplyFunction(thunk, noArgs, runner);
            // If thunk escaped or errored, still call after, then propagate
            if (Lv.ShouldReturn(result))
            {
                DataList aResult = ApplyFunction(after, noArgs, runner);
                // If after itself errors, return that error instead
                if (Lv.IsErr(aResult)) return aResult;
                return result;
            }
            // Normal path: call after, return thunk result
            DataList afterResult = ApplyFunction(after, noArgs, runner);
            if (Lv.ShouldReturn(afterResult)) return afterResult;
            return result;
        }

        private static DataList BProcedureQ(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("procedure?: requires 1 argument");
            string t = Lv.Tag(a[0]);
            return Lv.Bool(t == Lv.TFn || t == Lv.TBuiltin || t == Lv.TCont);
        }

        // ---- Arithmetic ----

        private static DataList BAdd(DataList[] args)
        {
            if (args.Length == 0) return Lv.Int(0);
            for (int i = 0; i < args.Length; i++)
                if (!Lv.IsNumber(args[i])) return Lv.Err("+: expected number");

            if (Lv.AnyFloat(args))
            {
                double sum = 0;
                for (int i = 0; i < args.Length; i++) sum += Lv.ToNumber(args[i]);
                return Lv.Float(sum);
            }
            int isum = 0;
            for (int i = 0; i < args.Length; i++) isum += Lv.GetInt(args[i]);
            return Lv.Int(isum);
        }

        private static DataList BSub(DataList[] args)
        {
            if (args.Length == 0) return Lv.Err("-: requires at least 1 argument");
            for (int i = 0; i < args.Length; i++)
                if (!Lv.IsNumber(args[i])) return Lv.Err("-: expected number");
            if (args.Length == 1)
            {
                if (Lv.Tag(args[0]) == Lv.TFloat) return Lv.Float(-Lv.GetFloat(args[0]));
                return Lv.Int(-Lv.GetInt(args[0]));
            }
            if (Lv.AnyFloat(args))
            {
                double r = Lv.ToNumber(args[0]);
                for (int i = 1; i < args.Length; i++) r -= Lv.ToNumber(args[i]);
                return Lv.Float(r);
            }
            int ir = Lv.GetInt(args[0]);
            for (int i = 1; i < args.Length; i++) ir -= Lv.GetInt(args[i]);
            return Lv.Int(ir);
        }

        private static DataList BMul(DataList[] args)
        {
            if (args.Length == 0) return Lv.Int(1);
            for (int i = 0; i < args.Length; i++)
                if (!Lv.IsNumber(args[i])) return Lv.Err("*: expected number");
            if (Lv.AnyFloat(args))
            {
                double r = 1;
                for (int i = 0; i < args.Length; i++) r *= Lv.ToNumber(args[i]);
                return Lv.Float(r);
            }
            int ir = 1;
            for (int i = 0; i < args.Length; i++) ir *= Lv.GetInt(args[i]);
            return Lv.Int(ir);
        }

        private static DataList BDiv(DataList[] args)
        {
            if (args.Length == 0) return Lv.Err("/: requires at least 1 argument");
            for (int i = 0; i < args.Length; i++)
                if (!Lv.IsNumber(args[i])) return Lv.Err("/: expected numbers");

            // (/ x) => 1/x
            if (args.Length == 1)
            {
                double d = Lv.ToNumber(args[0]);
                if (d == 0) return Lv.Err("/: division by zero");
                return Lv.Float(1.0 / d);
            }

            // (/ a b ...) => a/b/...
            if (Lv.AnyFloat(args))
            {
                double r = Lv.ToNumber(args[0]);
                for (int i = 1; i < args.Length; i++)
                {
                    double d = Lv.ToNumber(args[i]);
                    if (d == 0) return Lv.Err("/: division by zero");
                    r /= d;
                }
                return Lv.Float(r);
            }
            // All ints
            int ir = Lv.GetInt(args[0]);
            for (int i = 1; i < args.Length; i++)
            {
                int d = Lv.GetInt(args[i]);
                if (d == 0) return Lv.Err("/: division by zero");
                ir /= d;
            }
            return Lv.Int(ir);
        }

        private static DataList BMod(DataList[] args)
        {
            if (args.Length != 2) return Lv.Err("mod: requires 2 arguments");
            if (Lv.Tag(args[0]) != Lv.TInt || Lv.Tag(args[1]) != Lv.TInt)
                return Lv.Err("mod: expected integers");
            int b = Lv.GetInt(args[1]);
            if (b == 0) return Lv.Err("mod: division by zero");
            return Lv.Int(Lv.GetInt(args[0]) % b);
        }

        // ---- Comparison ----

        private static DataList BEq(DataList[] args)
        {
            if (args.Length < 2) return Lv.Err("=: requires at least 2 arguments");
            if (Lv.IsNumber(args[0]))
            {
                for (int i = 0; i < args.Length; i++)
                    if (!Lv.IsNumber(args[i])) return Lv.Err("=: expected number");
                for (int i = 0; i < args.Length - 1; i++)
                    if (Lv.ToNumber(args[i]) != Lv.ToNumber(args[i + 1])) return Lv.Bool(false);
                return Lv.Bool(true);
            }
            if (args.Length != 2) return Lv.Err("=: variadic form requires numbers");
            string t0 = Lv.Tag(args[0]), t1 = Lv.Tag(args[1]);
            if (t0 == Lv.TStr && t1 == Lv.TStr)
                return Lv.Bool(Lv.GetStr(args[0]) == Lv.GetStr(args[1]));
            if (t0 == Lv.TBool && t1 == Lv.TBool)
                return Lv.Bool(Lv.GetBool(args[0]) == Lv.GetBool(args[1]));
            if (t0 == Lv.TNil && t1 == Lv.TNil) return Lv.Bool(true);
            return Lv.Bool(false);
        }

        private static DataList BLt(DataList[] a)
        {
            if (a.Length < 2) return Lv.Err("<: requires at least 2 numbers");
            for (int i = 0; i < a.Length; i++)
                if (!Lv.IsNumber(a[i])) return Lv.Err("<: expected number");
            for (int i = 0; i < a.Length - 1; i++)
                if (!(Lv.ToNumber(a[i]) < Lv.ToNumber(a[i + 1]))) return Lv.Bool(false);
            return Lv.Bool(true);
        }

        private static DataList BGt(DataList[] a)
        {
            if (a.Length < 2) return Lv.Err(">: requires at least 2 numbers");
            for (int i = 0; i < a.Length; i++)
                if (!Lv.IsNumber(a[i])) return Lv.Err(">: expected number");
            for (int i = 0; i < a.Length - 1; i++)
                if (!(Lv.ToNumber(a[i]) > Lv.ToNumber(a[i + 1]))) return Lv.Bool(false);
            return Lv.Bool(true);
        }

        private static DataList BLe(DataList[] a)
        {
            if (a.Length < 2) return Lv.Err("<=: requires at least 2 numbers");
            for (int i = 0; i < a.Length; i++)
                if (!Lv.IsNumber(a[i])) return Lv.Err("<=: expected number");
            for (int i = 0; i < a.Length - 1; i++)
                if (!(Lv.ToNumber(a[i]) <= Lv.ToNumber(a[i + 1]))) return Lv.Bool(false);
            return Lv.Bool(true);
        }

        private static DataList BGe(DataList[] a)
        {
            if (a.Length < 2) return Lv.Err(">=: requires at least 2 numbers");
            for (int i = 0; i < a.Length; i++)
                if (!Lv.IsNumber(a[i])) return Lv.Err(">=: expected number");
            for (int i = 0; i < a.Length - 1; i++)
                if (!(Lv.ToNumber(a[i]) >= Lv.ToNumber(a[i + 1]))) return Lv.Bool(false);
            return Lv.Bool(true);
        }

        // ---- Logic ----

        private static DataList BNot(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("not: requires 1 argument");
            return Lv.Bool(!Lv.IsTruthy(a[0]));
        }

        // ---- List / Pair ops ----

        private static DataList BList(DataList[] args)
        {
            // Build a proper list as pair chain
            return Lv.PairList(args);
        }

        private static DataList BCar(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("car: requires 1 argument");
            string t = Lv.Tag(a[0]);
            if (t == Lv.TPair) return Lv.Car(a[0]);
            if (t == Lv.TList && Lv.ListCount(a[0]) > 0)
                return Lv.ListGet(a[0], 0);
            return Lv.Err("car: expected pair");
        }

        private static DataList BCdr(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("cdr: requires 1 argument");
            string t = Lv.Tag(a[0]);
            if (t == Lv.TPair) return Lv.Cdr(a[0]);
            if (t == Lv.TList)
            {
                int n = Lv.ListCount(a[0]);
                if (n == 0) return Lv.Err("cdr: expected pair");
                if (n == 1) return Lv.Nil();
                // Build remaining elements as pair chain
                var items = new DataList[n - 1];
                for (int i = 1; i < n; i++)
                    items[i - 1] = Lv.ListGet(a[0], i);
                return Lv.PairList(items);
            }
            return Lv.Err("cdr: expected pair");
        }

        private static DataList BCons(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("cons: requires 2 arguments");
            // cons always creates a pair
            return Lv.Pair(a[0], a[1]);
        }

        private static DataList BLength(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("length: requires 1 argument");
            string t = Lv.Tag(a[0]);
            if (t == Lv.TStr) return Lv.Int(Lv.GetStr(a[0]).Length);
            if (t == Lv.TNil) return Lv.Int(0);
            if (t == Lv.TList) return Lv.Int(Lv.ListCount(a[0]));
            if (t == Lv.TPair)
            {
                int len = Lv.PairListCount(a[0]);
                if (len < 0) return Lv.Err("length: not a proper list");
                return Lv.Int(len);
            }
            return Lv.Err("length: expected list or string");
        }

        private static DataList BNullQ(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("null?: requires 1 argument");
            return Lv.Bool(Lv.IsNil(a[0]));
        }

        private static DataList BPairQ(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("pair?: requires 1 argument");
            string t = Lv.Tag(a[0]);
            // A pair or a non-empty legacy list counts as a pair
            if (t == Lv.TPair) return Lv.Bool(true);
            if (t == Lv.TList && Lv.ListCount(a[0]) > 0) return Lv.Bool(true);
            return Lv.Bool(false);
        }

        private static DataList BListQ(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("list?: requires 1 argument");
            return Lv.Bool(Lv.IsProperList(a[0]));
        }

        private static DataList BTypeQ(DataList[] a, string t1, string t2)
        {
            if (a.Length != 1) return Lv.Err("type predicate requires 1 argument");
            string t = Lv.Tag(a[0]);
            return Lv.Bool(t == t1 || t == t2);
        }

        // ---- Mutation ----

        private static DataList BSetCar(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("set-car!: requires 2 arguments");
            if (Lv.Tag(a[0]) != Lv.TPair) return Lv.Err("set-car!: expected pair");
            Lv.SetCar(a[0], a[1]);
            return Lv.Nil();
        }

        private static DataList BSetCdr(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("set-cdr!: requires 2 arguments");
            if (Lv.Tag(a[0]) != Lv.TPair) return Lv.Err("set-cdr!: expected pair");
            Lv.SetCdr(a[0], a[1]);
            return Lv.Nil();
        }

        // ---- Higher-order functions ----

        /// <summary>
        /// (apply fn arg1 ... argN list)
        /// Calls fn with all arg1..argN prepended to the elements of list.
        /// </summary>
        [RecursiveMethod]
        private static DataList BApply(DataList[] a, LispRunner runner = null)
        {
            if (a.Length < 2) return Lv.Err("apply: requires at least 2 arguments");
            var func = a[0];
            string ft = Lv.Tag(func);
            if (ft != Lv.TBuiltin && ft != Lv.TFn)
                return Lv.Err("apply: first argument must be a procedure");

            // Last arg must be a list
            var lastArg = a[a.Length - 1];
            if (!Lv.IsProperList(lastArg))
                return Lv.Err("apply: last argument must be a proper list");

            // Count total args: (a.Length - 2) prefix args + list elements
            int listLen = Lv.Count(lastArg);
            int prefixCount = a.Length - 2;
            int totalArgs = prefixCount + listLen;

            var callArgs = new DataList[totalArgs];
            // Copy prefix args
            for (int i = 0; i < prefixCount; i++)
                callArgs[i] = a[i + 1];
            // Copy list elements
            for (int i = 0; i < listLen; i++)
                callArgs[prefixCount + i] = Lv.GetAt(lastArg, i);

            return ApplyFunction(func, callArgs, runner);
        }

        /// <summary>
        /// (map fn list1 list2 ...)
        /// Applies fn element-wise across the lists, returns list of results.
        /// </summary>
        [RecursiveMethod]
        private static DataList BMap(DataList[] a, LispRunner runner = null)
        {
            if (a.Length < 2) return Lv.Err("map: requires at least 2 arguments");
            var func = a[0];
            string ft = Lv.Tag(func);
            if (ft != Lv.TBuiltin && ft != Lv.TFn)
                return Lv.Err("map: first argument must be a procedure");

            int listCount = a.Length - 1;
            // Determine length from first list
            int len = Lv.Count(a[1]);
            // Verify all lists have same length
            for (int i = 2; i < a.Length; i++)
            {
                if (Lv.Count(a[i]) != len)
                    return Lv.Err("map: all lists must have the same length");
            }

            var results = new DataList[len];
            for (int i = 0; i < len; i++)
            {
                var callArgs = new DataList[listCount];
                for (int j = 0; j < listCount; j++)
                    callArgs[j] = Lv.GetAt(a[j + 1], i);

                var result = ApplyFunction(func, callArgs, runner);
                if (Lv.ShouldReturn(result)) return result;
                results[i] = result;
            }

            return Lv.PairList(results);
        }

        /// <summary>
        /// (for-each fn list1 list2 ...)
        /// Like map but discards results, returns nil.
        /// </summary>
        [RecursiveMethod]
        private static DataList BForEach(DataList[] a, LispRunner runner = null)
        {
            if (a.Length < 2) return Lv.Err("for-each: requires at least 2 arguments");
            var func = a[0];
            string ft = Lv.Tag(func);
            if (ft != Lv.TBuiltin && ft != Lv.TFn)
                return Lv.Err("for-each: first argument must be a procedure");

            int listCount = a.Length - 1;
            int len = Lv.Count(a[1]);
            for (int i = 2; i < a.Length; i++)
            {
                if (Lv.Count(a[i]) != len)
                    return Lv.Err("for-each: all lists must have the same length");
            }

            for (int i = 0; i < len; i++)
            {
                var callArgs = new DataList[listCount];
                for (int j = 0; j < listCount; j++)
                    callArgs[j] = Lv.GetAt(a[j + 1], i);

                var result = ApplyFunction(func, callArgs, runner);
                if (Lv.ShouldReturn(result)) return result;
            }

            return Lv.Nil();
        }

        // ---- Equivalence predicates ----

        /// <summary>
        /// (eq? a b) — identity / shallow equality.
        /// Symbols with same name are eq?. Booleans with same value are eq?.
        /// nil is eq? to nil. Pairs/lists are eq? only if same reference.
        /// Numbers: same type and same value.
        /// </summary>
        private static DataList BEqQ(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("eq?: requires 2 arguments");
            return Lv.Bool(IsEq(a[0], a[1]));
        }

        private static bool IsEq(DataList x, DataList y)
        {
            // Same reference
            if (x == y) return true;
            string tx = Lv.Tag(x);
            string ty = Lv.Tag(y);
            if (tx != ty) return false;
            switch (tx)
            {
                case Lv.TNil: return true;
                case Lv.TBool: return Lv.GetBool(x) == Lv.GetBool(y);
                case Lv.TSym: return Lv.GetSym(x) == Lv.GetSym(y);
                case Lv.TInt: return Lv.GetInt(x) == Lv.GetInt(y);
                case Lv.TFloat: return Lv.GetFloat(x) == Lv.GetFloat(y);
                case Lv.TStr: return Lv.GetStr(x) == Lv.GetStr(y);
                // Pairs, lists, functions: reference identity only (already checked above)
                default: return false;
            }
        }

        /// <summary>
        /// (eqv? a b) — like eq? but numbers are compared by value
        /// regardless of exact/inexact if they have the same numeric value
        /// AND the same exactness (int vs float).
        /// </summary>
        private static DataList BEqvQ(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("eqv?: requires 2 arguments");
            return Lv.Bool(IsEqv(a[0], a[1]));
        }

        private static bool IsEqv(DataList x, DataList y)
        {
            // eqv? is the same as eq? for our implementation
            // since eq? already compares atoms by value
            return IsEq(x, y);
        }

        /// <summary>
        /// (equal? a b) — deep structural equality (recursive).
        /// </summary>
        [RecursiveMethod]
        private static DataList BEqualQ(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("equal?: requires 2 arguments");
            return Lv.Bool(IsEqual(a[0], a[1]));
        }

        [RecursiveMethod]
        private static bool IsEqual(DataList x, DataList y)
        {
            if (x == y) return true;
            string tx = Lv.Tag(x);
            string ty = Lv.Tag(y);

            // nil matches nil and empty list
            if (Lv.IsNil(x) && Lv.IsNil(y)) return true;

            // Numbers: compare across int/float
            if (Lv.IsNumber(x) && Lv.IsNumber(y))
                return Lv.ToNumber(x) == Lv.ToNumber(y);

            if (tx != ty)
            {
                // TPair vs TList: compare structurally
                if ((tx == Lv.TPair || tx == Lv.TList) && (ty == Lv.TPair || ty == Lv.TList))
                {
                    int cx = Lv.Count(x);
                    int cy = Lv.Count(y);
                    if (cx != cy) return false;
                    for (int i = 0; i < cx; i++)
                        if (!IsEqual(Lv.GetAt(x, i), Lv.GetAt(y, i))) return false;
                    return true;
                }
                return false;
            }

            switch (tx)
            {
                case Lv.TNil: return true;
                case Lv.TBool: return Lv.GetBool(x) == Lv.GetBool(y);
                case Lv.TSym: return Lv.GetSym(x) == Lv.GetSym(y);
                case Lv.TStr: return Lv.GetStr(x) == Lv.GetStr(y);
                case Lv.TPair:
                    return IsEqual(Lv.Car(x), Lv.Car(y)) && IsEqual(Lv.Cdr(x), Lv.Cdr(y));
                case Lv.TList:
                {
                    int lx = Lv.ListCount(x);
                    int ly = Lv.ListCount(y);
                    if (lx != ly) return false;
                    for (int i = 0; i < lx; i++)
                        if (!IsEqual(Lv.ListGet(x, i), Lv.ListGet(y, i))) return false;
                    return true;
                }
                case Lv.TVec:
                {
                    int vx = Lv.VecLen(x);
                    int vy = Lv.VecLen(y);
                    if (vx != vy) return false;
                    for (int i = 0; i < vx; i++)
                        if (!IsEqual(Lv.VecRef(x, i), Lv.VecRef(y, i))) return false;
                    return true;
                }
                default: return false;
            }
        }

        // ---- List library (Phase 1.6) ----

        /// <summary>
        /// cXXXr dispatch: parses the name string (e.g. "cadr" -> cdr then car).
        /// The letters between c and r are processed right-to-left:
        /// 'a' = car, 'd' = cdr.
        /// </summary>
        private static DataList BCxxxr(string name, DataList[] a)
        {
            if (a.Length != 1) return Lv.Err(name + ": requires 1 argument");
            DataList val = a[0];
            // Process letters between 'c' and 'r' from right to left
            for (int i = name.Length - 2; i >= 1; i--)
            {
                char ch = name[i];
                string t = Lv.Tag(val);
                if (t == Lv.TPair)
                {
                    if (ch == 'a') val = Lv.Car(val);
                    else if (ch == 'd') val = Lv.Cdr(val);
                    else return Lv.Err(name + ": invalid composition");
                }
                else if (t == Lv.TList)
                {
                    if (ch == 'a')
                    {
                        if (Lv.ListCount(val) == 0) return Lv.Err(name + ": cannot take car of empty list");
                        val = Lv.ListGet(val, 0);
                    }
                    else if (ch == 'd')
                    {
                        int n = Lv.ListCount(val);
                        if (n == 0) return Lv.Err(name + ": cannot take cdr of empty list");
                        if (n == 1) val = Lv.Nil();
                        else
                        {
                            var items = new DataList[n - 1];
                            for (int j = 1; j < n; j++)
                                items[j - 1] = Lv.ListGet(val, j);
                            val = Lv.PairList(items);
                        }
                    }
                    else return Lv.Err(name + ": invalid composition");
                }
                else
                {
                    return Lv.Err(name + ": expected pair");
                }
            }
            return val;
        }

        /// <summary>
        /// (append list1 list2 ... listN)
        /// Returns a newly allocated list that is the concatenation of the lists.
        /// The last argument can be any object (becomes the cdr of the last pair).
        /// (append) returns nil.
        /// </summary>
        private static DataList BAppend(DataList[] a)
        {
            if (a.Length == 0) return Lv.Nil();
            if (a.Length == 1) return a[0];

            // Collect all elements from all lists except the last,
            // then attach the last argument as the tail.
            var elements = new DataList[0];
            for (int i = 0; i < a.Length - 1; i++)
            {
                if (!Lv.IsProperList(a[i]))
                    return Lv.Err("append: expected proper list, got " + LispPrinter.Print(a[i]));
                int len = Lv.Count(a[i]);
                var newElems = new DataList[elements.Length + len];
                for (int j = 0; j < elements.Length; j++)
                    newElems[j] = elements[j];
                for (int j = 0; j < len; j++)
                    newElems[elements.Length + j] = Lv.GetAt(a[i], j);
                elements = newElems;
            }

            // Last argument: if it's a proper list, append its elements too.
            // If it's not, it becomes the cdr of the last pair (dotted).
            DataList last = a[a.Length - 1];
            if (elements.Length == 0) return last;

            if (Lv.IsNil(last))
            {
                return Lv.PairList(elements);
            }
            if (Lv.IsProperList(last))
            {
                int lastLen = Lv.Count(last);
                var all = new DataList[elements.Length + lastLen];
                for (int j = 0; j < elements.Length; j++)
                    all[j] = elements[j];
                for (int j = 0; j < lastLen; j++)
                    all[elements.Length + j] = Lv.GetAt(last, j);
                return Lv.PairList(all);
            }

            // Last is not a list — build pairs ending with last as cdr
            DataList result = last;
            for (int i = elements.Length - 1; i >= 0; i--)
                result = Lv.Pair(elements[i], result);
            return result;
        }

        /// <summary>
        /// (reverse list) — returns a newly allocated list with elements in reverse.
        /// </summary>
        private static DataList BReverse(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("reverse: requires 1 argument");
            if (!Lv.IsProperList(a[0]))
                return Lv.Err("reverse: expected proper list");
            int len = Lv.Count(a[0]);
            if (len == 0) return Lv.Nil();
            var items = new DataList[len];
            for (int i = 0; i < len; i++)
                items[i] = Lv.GetAt(a[0], len - 1 - i);
            return Lv.PairList(items);
        }

        /// <summary>
        /// (list-tail list k) — returns the sublist obtained by dropping the first k elements.
        /// </summary>
        private static DataList BListTail(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("list-tail: requires 2 arguments");
            if (!Lv.IsNumber(a[1])) return Lv.Err("list-tail: second argument must be a number");
            int k = (int)Lv.ToNumber(a[1]);
            DataList cur = a[0];
            for (int i = 0; i < k; i++)
            {
                string t = Lv.Tag(cur);
                if (t == Lv.TPair)
                    cur = Lv.Cdr(cur);
                else if (t == Lv.TList && Lv.ListCount(cur) > 0)
                {
                    // Convert remaining TList to pair chain, then continue
                    int n = Lv.ListCount(cur);
                    var items = new DataList[n - 1];
                    for (int j = 1; j < n; j++)
                        items[j - 1] = Lv.ListGet(cur, j);
                    cur = Lv.PairList(items);
                }
                else
                    return Lv.Err("list-tail: index out of range");
            }
            return cur;
        }

        /// <summary>
        /// (list-ref list k) — returns the element at index k (0-based).
        /// </summary>
        private static DataList BListRef(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("list-ref: requires 2 arguments");
            if (!Lv.IsNumber(a[1])) return Lv.Err("list-ref: second argument must be a number");
            int k = (int)Lv.ToNumber(a[1]);
            int len = Lv.Count(a[0]);
            if (k < 0 || k >= len) return Lv.Err("list-ref: index out of range");
            return Lv.GetAt(a[0], k);
        }

        /// <summary>
        /// memq/memv/member — search list for an element.
        /// mode: 0=eq?, 1=eqv?, 2=equal?
        /// Returns the sublist starting at the found element, or #f.
        /// </summary>
        private static DataList MemHelper(DataList key, DataList lst, int mode)
        {
            DataList cur = lst;
            while (true)
            {
                if (Lv.IsNil(cur)) return Lv.Bool(false);
                string t = Lv.Tag(cur);
                if (t == Lv.TPair)
                {
                    DataList elem = Lv.Car(cur);
                    bool match = false;
                    if (mode == 0) match = IsEq(key, elem);
                    else if (mode == 1) match = IsEqv(key, elem);
                    else match = IsEqual(key, elem);
                    if (match) return cur;
                    cur = Lv.Cdr(cur);
                }
                else if (t == Lv.TList)
                {
                    // Legacy TList: convert to pair chain, then search
                    int n = Lv.ListCount(cur);
                    if (n == 0) return Lv.Bool(false);
                    var items = new DataList[n];
                    for (int i = 0; i < n; i++)
                        items[i] = Lv.ListGet(cur, i);
                    cur = Lv.PairList(items);
                    // Continue loop — now it's a pair chain
                }
                else
                {
                    return Lv.Bool(false);
                }
            }
        }

        private static DataList BMemq(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("memq: requires 2 arguments");
            return MemHelper(a[0], a[1], 0);
        }

        private static DataList BMemv(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("memv: requires 2 arguments");
            return MemHelper(a[0], a[1], 1);
        }

        private static DataList BMember(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("member: requires 2 arguments");
            return MemHelper(a[0], a[1], 2);
        }

        /// <summary>
        /// assq/assv/assoc — search association list (list of pairs).
        /// mode: 0=eq?, 1=eqv?, 2=equal?
        /// Returns the first pair whose car matches key, or #f.
        /// </summary>
        private static DataList AssHelper(DataList key, DataList alist, int mode)
        {
            int len = Lv.Count(alist);
            for (int i = 0; i < len; i++)
            {
                DataList entry = Lv.GetAt(alist, i);
                string et = Lv.Tag(entry);
                if (et != Lv.TPair && !(et == Lv.TList && Lv.ListCount(entry) > 0))
                    return Lv.Err("assoc: entries must be pairs");

                DataList entryKey;
                if (et == Lv.TPair) entryKey = Lv.Car(entry);
                else entryKey = Lv.ListGet(entry, 0);

                bool match = false;
                if (mode == 0) match = IsEq(key, entryKey);
                else if (mode == 1) match = IsEqv(key, entryKey);
                else match = IsEqual(key, entryKey);
                if (match) return entry;
            }
            return Lv.Bool(false);
        }

        private static DataList BAssq(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("assq: requires 2 arguments");
            return AssHelper(a[0], a[1], 0);
        }

        private static DataList BAssv(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("assv: requires 2 arguments");
            return AssHelper(a[0], a[1], 1);
        }

        private static DataList BAssoc(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("assoc: requires 2 arguments");
            return AssHelper(a[0], a[1], 2);
        }

        // ---- Numeric library (Phase 1.9) ----

        private static DataList BIntegerQ(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("integer?: requires 1 argument");
            string t = Lv.Tag(a[0]);
            if (t == Lv.TInt) return Lv.Bool(true);
            if (t == Lv.TFloat)
            {
                double d = Lv.GetFloat(a[0]);
                return Lv.Bool(d == System.Math.Floor(d) && !double.IsInfinity(d) && !double.IsNaN(d));
            }
            return Lv.Bool(false);
        }

        private static DataList BZeroQ(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("zero?: requires 1 argument");
            if (!Lv.IsNumber(a[0])) return Lv.Err("zero?: expected number");
            return Lv.Bool(Lv.ToNumber(a[0]) == 0);
        }

        private static DataList BPositiveQ(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("positive?: requires 1 argument");
            if (!Lv.IsNumber(a[0])) return Lv.Err("positive?: expected number");
            return Lv.Bool(Lv.ToNumber(a[0]) > 0);
        }

        private static DataList BNegativeQ(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("negative?: requires 1 argument");
            if (!Lv.IsNumber(a[0])) return Lv.Err("negative?: expected number");
            return Lv.Bool(Lv.ToNumber(a[0]) < 0);
        }

        private static DataList BOddQ(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("odd?: requires 1 argument");
            if (Lv.Tag(a[0]) != Lv.TInt) return Lv.Err("odd?: expected integer");
            return Lv.Bool(Lv.GetInt(a[0]) % 2 != 0);
        }

        private static DataList BEvenQ(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("even?: requires 1 argument");
            if (Lv.Tag(a[0]) != Lv.TInt) return Lv.Err("even?: expected integer");
            return Lv.Bool(Lv.GetInt(a[0]) % 2 == 0);
        }

        private static DataList BExactQ(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("exact?: requires 1 argument");
            // In our implementation, int is exact, float is inexact
            return Lv.Bool(Lv.Tag(a[0]) == Lv.TInt);
        }

        private static DataList BInexactQ(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("inexact?: requires 1 argument");
            return Lv.Bool(Lv.Tag(a[0]) == Lv.TFloat);
        }

        private static DataList BAbs(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("abs: requires 1 argument");
            if (!Lv.IsNumber(a[0])) return Lv.Err("abs: expected number");
            if (Lv.Tag(a[0]) == Lv.TInt)
                return Lv.Int(System.Math.Abs(Lv.GetInt(a[0])));
            return Lv.Float(System.Math.Abs(Lv.GetFloat(a[0])));
        }

        /// <summary>
        /// (max n1 n2 ...) / (min n1 n2 ...)
        /// isMax=true for max, false for min.
        /// </summary>
        private static DataList BMaxMin(DataList[] a, bool isMax)
        {
            string fname = isMax ? "max" : "min";
            if (a.Length == 0) return Lv.Err(fname + ": requires at least 1 argument");
            for (int i = 0; i < a.Length; i++)
                if (!Lv.IsNumber(a[i])) return Lv.Err(fname + ": expected number");

            double best = Lv.ToNumber(a[0]);
            for (int i = 1; i < a.Length; i++)
            {
                double v = Lv.ToNumber(a[i]);
                if (isMax) { if (v > best) best = v; }
                else { if (v < best) best = v; }
            }

            if (Lv.AnyFloat(a)) return Lv.Float(best);
            return Lv.Int((int)best);
        }

        private static DataList BQuotient(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("quotient: requires 2 arguments");
            if (!Lv.IsNumber(a[0]) || !Lv.IsNumber(a[1]))
                return Lv.Err("quotient: expected numbers");
            double d = Lv.ToNumber(a[1]);
            if (d == 0) return Lv.Err("quotient: division by zero");
            double result = Lv.ToNumber(a[0]) / d;
            // truncate toward zero
            return Lv.Int((int)result);
        }

        private static DataList BRemainder(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("remainder: requires 2 arguments");
            if (!Lv.IsNumber(a[0]) || !Lv.IsNumber(a[1]))
                return Lv.Err("remainder: expected numbers");
            double d = Lv.ToNumber(a[1]);
            if (d == 0) return Lv.Err("remainder: division by zero");
            // remainder: sign matches dividend (C# % behavior)
            int n = (int)Lv.ToNumber(a[0]);
            int dd = (int)d;
            return Lv.Int(n % dd);
        }

        private static DataList BModulo(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("modulo: requires 2 arguments");
            if (!Lv.IsNumber(a[0]) || !Lv.IsNumber(a[1]))
                return Lv.Err("modulo: expected numbers");
            double d = Lv.ToNumber(a[1]);
            if (d == 0) return Lv.Err("modulo: division by zero");
            // modulo: sign matches divisor (R5RS semantics)
            int n = (int)Lv.ToNumber(a[0]);
            int dd = (int)d;
            int r = n % dd;
            if (r != 0 && ((r > 0) != (dd > 0))) r += dd;
            return Lv.Int(r);
        }

        private static int GcdHelper(int a, int b)
        {
            if (a < 0) a = -a;
            if (b < 0) b = -b;
            while (b != 0)
            {
                int tmp = b;
                b = a % b;
                a = tmp;
            }
            return a;
        }

        private static DataList BGcd(DataList[] a)
        {
            if (a.Length == 0) return Lv.Int(0);
            for (int i = 0; i < a.Length; i++)
                if (!Lv.IsNumber(a[i])) return Lv.Err("gcd: expected numbers");
            int result = (int)Lv.ToNumber(a[0]);
            if (result < 0) result = -result;
            for (int i = 1; i < a.Length; i++)
                result = GcdHelper(result, (int)Lv.ToNumber(a[i]));
            return Lv.Int(result);
        }

        private static DataList BLcm(DataList[] a)
        {
            if (a.Length == 0) return Lv.Int(1);
            for (int i = 0; i < a.Length; i++)
                if (!Lv.IsNumber(a[i])) return Lv.Err("lcm: expected numbers");
            int result = (int)Lv.ToNumber(a[0]);
            if (result < 0) result = -result;
            for (int i = 1; i < a.Length; i++)
            {
                int b = (int)Lv.ToNumber(a[i]);
                if (b < 0) b = -b;
                if (result == 0 || b == 0) { result = 0; }
                else { result = result / GcdHelper(result, b) * b; }
            }
            return Lv.Int(result);
        }

        /// <summary>
        /// floor/ceiling/truncate/round — mode 0/1/2/3.
        /// </summary>
        private static DataList BMathRound(DataList[] a, int mode)
        {
            if (a.Length != 1) return Lv.Err("rounding function: requires 1 argument");
            if (!Lv.IsNumber(a[0])) return Lv.Err("rounding function: expected number");
            if (Lv.Tag(a[0]) == Lv.TInt) return a[0]; // already exact integer
            double d = Lv.GetFloat(a[0]);
            double r = d;
            if (mode == 0) r = System.Math.Floor(d);
            else if (mode == 1) r = System.Math.Ceiling(d);
            else if (mode == 2) r = System.Math.Truncate(d);
            else r = System.Math.Round(d, System.MidpointRounding.ToEven);
            // R5RS: rounding functions return exact iff argument is exact.
            // Since we got a float, return int if it's a whole number.
            return Lv.Int((int)r);
        }

        /// <summary>
        /// Single-argument math functions: sqrt(0), exp(1), log(2),
        /// sin(3), cos(4), tan(5), asin(6), acos(7).
        /// </summary>
        private static DataList BMath1(DataList[] a, int fn)
        {
            if (a.Length != 1) return Lv.Err("math function: requires 1 argument");
            if (!Lv.IsNumber(a[0])) return Lv.Err("math function: expected number");
            double d = Lv.ToNumber(a[0]);
            double r = 0;
            if (fn == 0) r = System.Math.Sqrt(d);
            else if (fn == 1) r = System.Math.Exp(d);
            else if (fn == 2) r = System.Math.Log(d);
            else if (fn == 3) r = System.Math.Sin(d);
            else if (fn == 4) r = System.Math.Cos(d);
            else if (fn == 5) r = System.Math.Tan(d);
            else if (fn == 6) r = System.Math.Asin(d);
            else if (fn == 7) r = System.Math.Acos(d);
            return Lv.Float(r);
        }

        private static DataList BExpt(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("expt: requires 2 arguments");
            if (!Lv.IsNumber(a[0]) || !Lv.IsNumber(a[1]))
                return Lv.Err("expt: expected numbers");
            double b = Lv.ToNumber(a[0]);
            double e = Lv.ToNumber(a[1]);
            double r = System.Math.Pow(b, e);
            // Return int if both args are int and result fits
            if (Lv.Tag(a[0]) == Lv.TInt && Lv.Tag(a[1]) == Lv.TInt
                && e >= 0 && r == System.Math.Floor(r)
                && r >= int.MinValue && r <= int.MaxValue)
                return Lv.Int((int)r);
            return Lv.Float(r);
        }

        /// <summary>
        /// (atan y) or (atan y x) — supports both 1 and 2 arg forms.
        /// </summary>
        private static DataList BAtan(DataList[] a)
        {
            if (a.Length == 1)
            {
                if (!Lv.IsNumber(a[0])) return Lv.Err("atan: expected number");
                return Lv.Float(System.Math.Atan(Lv.ToNumber(a[0])));
            }
            if (a.Length == 2)
            {
                if (!Lv.IsNumber(a[0]) || !Lv.IsNumber(a[1]))
                    return Lv.Err("atan: expected numbers");
                return Lv.Float(System.Math.Atan2(Lv.ToNumber(a[0]), Lv.ToNumber(a[1])));
            }
            return Lv.Err("atan: requires 1 or 2 arguments");
        }

        private static DataList BExactToInexact(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("exact->inexact: requires 1 argument");
            if (!Lv.IsNumber(a[0])) return Lv.Err("exact->inexact: expected number");
            return Lv.Float(Lv.ToNumber(a[0]));
        }

        private static DataList BInexactToExact(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("inexact->exact: requires 1 argument");
            if (!Lv.IsNumber(a[0])) return Lv.Err("inexact->exact: expected number");
            return Lv.Int((int)Lv.ToNumber(a[0]));
        }

        // ---- String ops ----

        private static DataList BStrAppend(DataList[] a)
        {
            string r = "";
            for (int i = 0; i < a.Length; i++)
            {
                if (Lv.Tag(a[i]) != Lv.TStr) return Lv.Err("string-append: expected strings");
                r += Lv.GetStr(a[i]);
            }
            return Lv.Str(r);
        }

        private static DataList BStrLen(DataList[] a)
        {
            if (a.Length != 1 || Lv.Tag(a[0]) != Lv.TStr)
                return Lv.Err("string-length: requires 1 string");
            return Lv.Int(Lv.GetStr(a[0]).Length);
        }

        private static DataList BNumToStr(DataList[] a)
        {
            if (a.Length != 1 || !Lv.IsNumber(a[0]))
                return Lv.Err("number->string: requires 1 number");
            if (Lv.Tag(a[0]) == Lv.TInt) return Lv.Str(Lv.GetInt(a[0]).ToString());
            return Lv.Str(Lv.GetFloat(a[0]).ToString());
        }

        private static DataList BStrToNum(DataList[] a)
        {
            if (a.Length != 1 || Lv.Tag(a[0]) != Lv.TStr)
                return Lv.Err("string->number: requires 1 string");
            string s = Lv.GetStr(a[0]);
            int iv;
            if (int.TryParse(s, out iv)) return Lv.Int(iv);
            double dv;
            if (double.TryParse(s, out dv)) return Lv.Float(dv);
            return Lv.Bool(false);
        }

        private static DataList BDisplay(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("display: requires 1 argument");
            Debug.Log("[Lisp] " + LispPrinter.Display(a[0]));
            return Lv.Nil();
        }

        private static DataList BWrite(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("write: requires 1 argument");
            Debug.Log("[Lisp] " + LispPrinter.Print(a[0]));
            return Lv.Nil();
        }

        private static DataList BNewline(DataList[] a)
        {
            if (a.Length != 0) return Lv.Err("newline: takes no arguments");
            Debug.Log("[Lisp] ");
            return Lv.Nil();
        }

        private static DataList BSymToStr(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("symbol->string: requires 1 argument");
            if (Lv.Tag(a[0]) != Lv.TSym) return Lv.Err("symbol->string: expected symbol");
            return Lv.Str(Lv.GetSym(a[0]));
        }

        private static DataList BStrToSym(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("string->symbol: requires 1 argument");
            if (Lv.Tag(a[0]) != Lv.TStr) return Lv.Err("string->symbol: expected string");
            return Lv.Sym(Lv.GetStr(a[0]));
        }

        // ======== Character procedures (Phase 2.1) ========

        /// <summary>
        /// Character comparison. mode: 0 =, 1 <, 2 >, 3 <=, 4 >=
        /// </summary>
        private static DataList BCharCmp(DataList[] a, int mode)
        {
            if (a.Length != 2) return Lv.Err("char comparison: requires 2 arguments");
            if (Lv.Tag(a[0]) != Lv.TChar) return Lv.Err("char comparison: expected char");
            if (Lv.Tag(a[1]) != Lv.TChar) return Lv.Err("char comparison: expected char");
            string c1 = Lv.GetChar(a[0]);
            string c2 = Lv.GetChar(a[1]);
            int v1 = (c1.Length > 0) ? (int)c1[0] : 0;
            int v2 = (c2.Length > 0) ? (int)c2[0] : 0;
            bool result = false;
            switch (mode)
            {
                case 0: result = v1 == v2; break;
                case 1: result = v1 < v2; break;
                case 2: result = v1 > v2; break;
                case 3: result = v1 <= v2; break;
                case 4: result = v1 >= v2; break;
            }
            return Lv.Bool(result);
        }

        /// <summary>
        /// Case-insensitive character comparison. mode: 0 =, 1 <, 2 >, 3 <=, 4 >=
        /// </summary>
        private static DataList BCharCiCmp(DataList[] a, int mode)
        {
            if (a.Length != 2) return Lv.Err("char-ci comparison: requires 2 arguments");
            if (Lv.Tag(a[0]) != Lv.TChar) return Lv.Err("char-ci comparison: expected char");
            if (Lv.Tag(a[1]) != Lv.TChar) return Lv.Err("char-ci comparison: expected char");
            string c1 = Lv.GetChar(a[0]).ToLower();
            string c2 = Lv.GetChar(a[1]).ToLower();
            int v1 = (c1.Length > 0) ? (int)c1[0] : 0;
            int v2 = (c2.Length > 0) ? (int)c2[0] : 0;
            bool result = false;
            switch (mode)
            {
                case 0: result = v1 == v2; break;
                case 1: result = v1 < v2; break;
                case 2: result = v1 > v2; break;
                case 3: result = v1 <= v2; break;
                case 4: result = v1 >= v2; break;
            }
            return Lv.Bool(result);
        }

        /// <summary>
        /// Character classification. mode: 0 alphabetic, 1 numeric, 2 whitespace
        /// </summary>
        private static DataList BCharClass(DataList[] a, int mode)
        {
            if (a.Length != 1) return Lv.Err("char classification: requires 1 argument");
            if (Lv.Tag(a[0]) != Lv.TChar) return Lv.Err("char classification: expected char");
            string cs = Lv.GetChar(a[0]);
            if (cs.Length == 0) return Lv.Bool(false);
            char c = cs[0];
            switch (mode)
            {
                case 0: return Lv.Bool((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));
                case 1: return Lv.Bool(c >= '0' && c <= '9');
                case 2: return Lv.Bool(c == ' ' || c == '\t' || c == '\n' || c == '\r');
                default: return Lv.Bool(false);
            }
        }

        /// <summary>
        /// Character case conversion. toUpper: true = upcase, false = downcase
        /// </summary>
        private static DataList BCharCase(DataList[] a, bool toUpper)
        {
            if (a.Length != 1) return Lv.Err("char case: requires 1 argument");
            if (Lv.Tag(a[0]) != Lv.TChar) return Lv.Err("char case: expected char");
            string cs = Lv.GetChar(a[0]);
            if (toUpper)
                return Lv.Char(cs.ToUpper());
            else
                return Lv.Char(cs.ToLower());
        }

        private static DataList BCharToInt(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("char->integer: requires 1 argument");
            if (Lv.Tag(a[0]) != Lv.TChar) return Lv.Err("char->integer: expected char");
            string cs = Lv.GetChar(a[0]);
            if (cs.Length == 0) return Lv.Int(0);
            return Lv.Int((int)cs[0]);
        }

        private static DataList BIntToChar(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("integer->char: requires 1 argument");
            string tag = Lv.Tag(a[0]);
            if (tag != Lv.TInt) return Lv.Err("integer->char: expected integer");
            int n = Lv.GetInt(a[0]);
            return Lv.Char(((char)n).ToString());
        }

        // ======== String expansion (Phase 2.2) ========

        private static DataList BMakeString(DataList[] a)
        {
            if (a.Length < 1 || a.Length > 2) return Lv.Err("make-string: requires 1-2 arguments");
            if (Lv.Tag(a[0]) != Lv.TInt) return Lv.Err("make-string: first arg must be integer");
            int n = Lv.GetInt(a[0]);
            if (n < 0) return Lv.Err("make-string: length must be non-negative");
            char fill = (a.Length == 2 && Lv.Tag(a[1]) == Lv.TChar && Lv.GetChar(a[1]).Length > 0)
                ? Lv.GetChar(a[1])[0] : '\0';
            string result = "";
            for (int i = 0; i < n; i++) result += fill;
            return Lv.Str(result);
        }

        private static DataList BString(DataList[] a)
        {
            string result = "";
            for (int i = 0; i < a.Length; i++)
            {
                if (Lv.Tag(a[i]) != Lv.TChar) return Lv.Err("string: expected char argument");
                result += Lv.GetChar(a[i]);
            }
            return Lv.Str(result);
        }

        private static DataList BStringRef(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("string-ref: requires 2 arguments");
            if (Lv.Tag(a[0]) != Lv.TStr) return Lv.Err("string-ref: first arg must be string");
            if (Lv.Tag(a[1]) != Lv.TInt) return Lv.Err("string-ref: second arg must be integer");
            string s = Lv.GetStr(a[0]);
            int k = Lv.GetInt(a[1]);
            if (k < 0 || k >= s.Length) return Lv.Err("string-ref: index out of range");
            return Lv.Char(s[k].ToString());
        }

        private static DataList BStringSet(DataList[] a)
        {
            if (a.Length != 3) return Lv.Err("string-set!: requires 3 arguments");
            if (Lv.Tag(a[0]) != Lv.TStr) return Lv.Err("string-set!: first arg must be string");
            if (Lv.Tag(a[1]) != Lv.TInt) return Lv.Err("string-set!: second arg must be integer");
            if (Lv.Tag(a[2]) != Lv.TChar) return Lv.Err("string-set!: third arg must be char");
            string s = Lv.GetStr(a[0]);
            int k = Lv.GetInt(a[1]);
            if (k < 0 || k >= s.Length) return Lv.Err("string-set!: index out of range");
            string ch = Lv.GetChar(a[2]);
            // Build new string with replaced char
            string ns = "";
            for (int i = 0; i < s.Length; i++)
            {
                if (i == k) ns += ch;
                else ns += s[i];
            }
            // Mutate the DataList in-place: set element [1] to new string
            a[0][1] = new DataToken(ns);
            return Lv.Nil();
        }

        /// <summary>
        /// String comparison. mode: 0 =, 1 <, 2 >, 3 <=, 4 >=
        /// </summary>
        private static DataList BStrCmp(DataList[] a, int mode)
        {
            if (a.Length != 2) return Lv.Err("string comparison: requires 2 arguments");
            if (Lv.Tag(a[0]) != Lv.TStr) return Lv.Err("string comparison: expected string");
            if (Lv.Tag(a[1]) != Lv.TStr) return Lv.Err("string comparison: expected string");
            string s1 = Lv.GetStr(a[0]);
            string s2 = Lv.GetStr(a[1]);
            int cmp = string.Compare(s1, s2, System.StringComparison.Ordinal);
            switch (mode)
            {
                case 0: return Lv.Bool(cmp == 0);
                case 1: return Lv.Bool(cmp < 0);
                case 2: return Lv.Bool(cmp > 0);
                case 3: return Lv.Bool(cmp <= 0);
                case 4: return Lv.Bool(cmp >= 0);
                default: return Lv.Bool(false);
            }
        }

        /// <summary>
        /// Case-insensitive string comparison. mode: 0 =, 1 <, 2 >, 3 <=, 4 >=
        /// </summary>
        private static DataList BStrCiCmp(DataList[] a, int mode)
        {
            if (a.Length != 2) return Lv.Err("string-ci comparison: requires 2 arguments");
            if (Lv.Tag(a[0]) != Lv.TStr) return Lv.Err("string-ci comparison: expected string");
            if (Lv.Tag(a[1]) != Lv.TStr) return Lv.Err("string-ci comparison: expected string");
            string s1 = Lv.GetStr(a[0]).ToLower();
            string s2 = Lv.GetStr(a[1]).ToLower();
            int cmp = string.Compare(s1, s2, System.StringComparison.Ordinal);
            switch (mode)
            {
                case 0: return Lv.Bool(cmp == 0);
                case 1: return Lv.Bool(cmp < 0);
                case 2: return Lv.Bool(cmp > 0);
                case 3: return Lv.Bool(cmp <= 0);
                case 4: return Lv.Bool(cmp >= 0);
                default: return Lv.Bool(false);
            }
        }

        private static DataList BSubstring(DataList[] a)
        {
            if (a.Length != 3) return Lv.Err("substring: requires 3 arguments");
            if (Lv.Tag(a[0]) != Lv.TStr) return Lv.Err("substring: first arg must be string");
            if (Lv.Tag(a[1]) != Lv.TInt) return Lv.Err("substring: second arg must be integer");
            if (Lv.Tag(a[2]) != Lv.TInt) return Lv.Err("substring: third arg must be integer");
            string s = Lv.GetStr(a[0]);
            int start = Lv.GetInt(a[1]);
            int end = Lv.GetInt(a[2]);
            if (start < 0 || end > s.Length || start > end)
                return Lv.Err("substring: index out of range");
            return Lv.Str(s.Substring(start, end - start));
        }

        private static DataList BStringCopy(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("string-copy: requires 1 argument");
            if (Lv.Tag(a[0]) != Lv.TStr) return Lv.Err("string-copy: expected string");
            return Lv.Str(Lv.GetStr(a[0]));
        }

        private static DataList BStringToList(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("string->list: requires 1 argument");
            if (Lv.Tag(a[0]) != Lv.TStr) return Lv.Err("string->list: expected string");
            string s = Lv.GetStr(a[0]);
            if (s.Length == 0) return Lv.Nil();
            DataList result = Lv.Nil();
            // Build list from end to start using cons
            for (int i = s.Length - 1; i >= 0; i--)
            {
                result = Lv.Pair(Lv.Char(s[i].ToString()), result);
            }
            return result;
        }

        private static DataList BListToString(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("list->string: requires 1 argument");
            DataList lst = a[0];
            string result = "";
            while (!Lv.IsNil(lst))
            {
                if (Lv.Tag(lst) != Lv.TPair)
                    return Lv.Err("list->string: expected proper list of chars");
                DataList c = Lv.Car(lst);
                if (Lv.Tag(c) != Lv.TChar)
                    return Lv.Err("list->string: expected char element");
                result += Lv.GetChar(c);
                lst = Lv.Cdr(lst);
            }
            return Lv.Str(result);
        }

        // ======== Vector procedures (Phase 2.3) ========

        private static DataList BVector(DataList[] a)
        {
            return Lv.Vec(a);
        }

        private static DataList BMakeVector(DataList[] a)
        {
            if (a.Length < 1 || a.Length > 2) return Lv.Err("make-vector: requires 1-2 arguments");
            if (Lv.Tag(a[0]) != Lv.TInt) return Lv.Err("make-vector: first arg must be integer");
            int n = Lv.GetInt(a[0]);
            if (n < 0) return Lv.Err("make-vector: length must be non-negative");
            DataList fill = (a.Length == 2) ? a[1] : Lv.Nil();
            return Lv.VecMake(n, fill);
        }

        private static DataList BVectorRef(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("vector-ref: requires 2 arguments");
            if (Lv.Tag(a[0]) != Lv.TVec) return Lv.Err("vector-ref: first arg must be vector");
            if (Lv.Tag(a[1]) != Lv.TInt) return Lv.Err("vector-ref: second arg must be integer");
            int k = Lv.GetInt(a[1]);
            if (k < 0 || k >= Lv.VecLen(a[0])) return Lv.Err("vector-ref: index out of range");
            return Lv.VecRef(a[0], k);
        }

        private static DataList BVectorSet(DataList[] a)
        {
            if (a.Length != 3) return Lv.Err("vector-set!: requires 3 arguments");
            if (Lv.Tag(a[0]) != Lv.TVec) return Lv.Err("vector-set!: first arg must be vector");
            if (Lv.Tag(a[1]) != Lv.TInt) return Lv.Err("vector-set!: second arg must be integer");
            int k = Lv.GetInt(a[1]);
            if (k < 0 || k >= Lv.VecLen(a[0])) return Lv.Err("vector-set!: index out of range");
            Lv.VecSet(a[0], k, a[2]);
            return Lv.Nil();
        }

        private static DataList BVectorLength(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("vector-length: requires 1 argument");
            if (Lv.Tag(a[0]) != Lv.TVec) return Lv.Err("vector-length: expected vector");
            return Lv.Int(Lv.VecLen(a[0]));
        }

        private static DataList BVectorToList(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("vector->list: requires 1 argument");
            if (Lv.Tag(a[0]) != Lv.TVec) return Lv.Err("vector->list: expected vector");
            int len = Lv.VecLen(a[0]);
            if (len == 0) return Lv.Nil();
            DataList result = Lv.Nil();
            for (int i = len - 1; i >= 0; i--)
                result = Lv.Pair(Lv.VecRef(a[0], i), result);
            return result;
        }

        private static DataList BListToVector(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("list->vector: requires 1 argument");
            DataList lst = a[0];
            // Count elements first
            int count = 0;
            DataList tmp = lst;
            while (!Lv.IsNil(tmp))
            {
                if (Lv.Tag(tmp) != Lv.TPair)
                    return Lv.Err("list->vector: expected proper list");
                count++;
                tmp = Lv.Cdr(tmp);
            }
            DataList[] elems = new DataList[count];
            tmp = lst;
            for (int i = 0; i < count; i++)
            {
                elems[i] = Lv.Car(tmp);
                tmp = Lv.Cdr(tmp);
            }
            return Lv.Vec(elems);
        }

        private static DataList BVectorFill(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("vector-fill!: requires 2 arguments");
            if (Lv.Tag(a[0]) != Lv.TVec) return Lv.Err("vector-fill!: first arg must be vector");
            int len = Lv.VecLen(a[0]);
            for (int i = 0; i < len; i++)
                Lv.VecSet(a[0], i, a[1]);
            return Lv.Nil();
        }

        // ======== Reader (Phase 4.3) ========

        /// <summary>
        /// (read string) — parse a single Scheme expression from the given string.
        /// Returns the parsed AST as a Lisp value (not evaluated).
        /// This is R5RS's read procedure, adapted to take a string argument
        /// since there are no input ports in VRChat.
        /// </summary>
        private static DataList BRead(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("read: requires 1 argument (string)");
            if (Lv.Tag(a[0]) != Lv.TStr) return Lv.Err("read: argument must be a string");
            string source = Lv.GetStr(a[0]);
            if (string.IsNullOrEmpty(source)) return Lv.Eof();

            var tokens = LispTokenizer.Tokenize(source);
            if (tokens == null || tokens.Count == 0) return Lv.Eof();

            return LispParser.Parse(tokens);
        }

        // ======== Picture Language (SICP 2.2.4) ========

        // ---- Vector operations ----

        /// <summary>(make-vect x y) → vect</summary>
        private static DataList BMakeVect(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("make-vect: requires 2 arguments");
            double x = Lv.ToNumber(a[0]);
            double y = Lv.ToNumber(a[1]);
            return Lv.Vect(x, y);
        }

        /// <summary>(xcor-vect v) → number</summary>
        private static DataList BXcorVect(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("xcor-vect: requires 1 argument");
            if (!Lv.IsVect(a[0])) return Lv.Err("xcor-vect: argument must be a vect");
            return Lv.Float(Lv.VectX(a[0]));
        }

        /// <summary>(ycor-vect v) → number</summary>
        private static DataList BYcorVect(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("ycor-vect: requires 1 argument");
            if (!Lv.IsVect(a[0])) return Lv.Err("ycor-vect: argument must be a vect");
            return Lv.Float(Lv.VectY(a[0]));
        }

        /// <summary>(add-vect v1 v2) → vect</summary>
        private static DataList BAddVect(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("add-vect: requires 2 arguments");
            if (!Lv.IsVect(a[0]) || !Lv.IsVect(a[1])) return Lv.Err("add-vect: arguments must be vects");
            return Lv.Vect(Lv.VectX(a[0]) + Lv.VectX(a[1]),
                           Lv.VectY(a[0]) + Lv.VectY(a[1]));
        }

        /// <summary>(sub-vect v1 v2) → vect</summary>
        private static DataList BSubVect(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("sub-vect: requires 2 arguments");
            if (!Lv.IsVect(a[0]) || !Lv.IsVect(a[1])) return Lv.Err("sub-vect: arguments must be vects");
            return Lv.Vect(Lv.VectX(a[0]) - Lv.VectX(a[1]),
                           Lv.VectY(a[0]) - Lv.VectY(a[1]));
        }

        /// <summary>(scale-vect s v) → vect</summary>
        private static DataList BScaleVect(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("scale-vect: requires 2 arguments");
            double s = Lv.ToNumber(a[0]);
            if (!Lv.IsVect(a[1])) return Lv.Err("scale-vect: second argument must be a vect");
            return Lv.Vect(s * Lv.VectX(a[1]), s * Lv.VectY(a[1]));
        }

        // ---- Frame operations ----

        /// <summary>(make-frame origin edge1 edge2) → frame</summary>
        private static DataList BMakeFrame(DataList[] a)
        {
            if (a.Length != 3) return Lv.Err("make-frame: requires 3 arguments");
            if (!Lv.IsVect(a[0]) || !Lv.IsVect(a[1]) || !Lv.IsVect(a[2]))
                return Lv.Err("make-frame: all arguments must be vects");
            return Lv.Frame(a[0], a[1], a[2]);
        }

        /// <summary>(origin-frame f) → vect</summary>
        private static DataList BOriginFrame(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("origin-frame: requires 1 argument");
            if (!Lv.IsFrame(a[0])) return Lv.Err("origin-frame: argument must be a frame");
            return Lv.FrameOrigin(a[0]);
        }

        /// <summary>(edge1-frame f) → vect</summary>
        private static DataList BEdge1Frame(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("edge1-frame: requires 1 argument");
            if (!Lv.IsFrame(a[0])) return Lv.Err("edge1-frame: argument must be a frame");
            return Lv.FrameEdge1(a[0]);
        }

        /// <summary>(edge2-frame f) → vect</summary>
        private static DataList BEdge2Frame(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("edge2-frame: requires 1 argument");
            if (!Lv.IsFrame(a[0])) return Lv.Err("edge2-frame: argument must be a frame");
            return Lv.FrameEdge2(a[0]);
        }

        // ---- Segment operations ----

        /// <summary>(make-segment start end) → segment</summary>
        private static DataList BMakeSegment(DataList[] a)
        {
            if (a.Length != 2) return Lv.Err("make-segment: requires 2 arguments");
            if (!Lv.IsVect(a[0]) || !Lv.IsVect(a[1]))
                return Lv.Err("make-segment: arguments must be vects");
            return Lv.Segment(a[0], a[1]);
        }

        /// <summary>(start-segment s) → vect</summary>
        private static DataList BStartSegment(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("start-segment: requires 1 argument");
            if (!Lv.IsSegment(a[0])) return Lv.Err("start-segment: argument must be a segment");
            return Lv.SegmentStart(a[0]);
        }

        /// <summary>(end-segment s) → vect</summary>
        private static DataList BEndSegment(DataList[] a)
        {
            if (a.Length != 1) return Lv.Err("end-segment: requires 1 argument");
            if (!Lv.IsSegment(a[0])) return Lv.Err("end-segment: argument must be a segment");
            return Lv.SegmentEnd(a[0]);
        }

        // ---- Canvas / Rendering operations ----

        /// <summary>
        /// (draw-line v1 v2) — draw a line from v1 to v2 on the canvas.
        /// v1, v2 are vects in pixel coordinates (0..width, 0..height).
        /// Uses Bresenham's line algorithm. Color is white.
        /// </summary>
        private static DataList BDrawLine(DataList[] a, LispRunner runner = null)
        {
            if (a.Length != 2) return Lv.Err("draw-line: requires 2 arguments");
            if (!Lv.IsVect(a[0]) || !Lv.IsVect(a[1]))
                return Lv.Err("draw-line: arguments must be vects");
            if (runner == null || runner.canvasPixels == null)
                return Lv.Err("draw-line: canvas not initialized");

            int x0 = (int)Lv.VectX(a[0]);
            int y0 = (int)Lv.VectY(a[0]);
            int x1 = (int)Lv.VectX(a[1]);
            int y1 = (int)Lv.VectY(a[1]);

            int cw = runner.canvasW;
            int ch = runner.canvasH;
            Color32[] pixels = runner.canvasPixels;

            // Bresenham's line algorithm
            int dx = x1 - x0;
            if (dx < 0) dx = -dx;
            int dy = y1 - y0;
            if (dy < 0) dy = -dy;
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            Color32 white = new Color32(255, 255, 255, 255);

            while (true)
            {
                // Plot pixel if in bounds
                if (x0 >= 0 && x0 < cw && y0 >= 0 && y0 < ch)
                    pixels[y0 * cw + x0] = white;

                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }

            return Lv.Nil();
        }

        /// <summary>
        /// (image-painter frame) — blit the source image into the given frame.
        /// Maps unit-square coordinates to frame coordinates, samples source image.
        /// This is the C# performance builtin that avoids per-pixel Lisp evaluation.
        /// Called directly as a builtin; returns nil after blitting.
        /// </summary>
        private static DataList BImagePainter(DataList[] a, LispRunner runner = null)
        {
            if (a.Length != 1) return Lv.Err("image-painter: requires 1 argument (frame)");
            if (!Lv.IsFrame(a[0])) return Lv.Err("image-painter: argument must be a frame");
            if (runner == null || runner.canvasPixels == null)
                return Lv.Err("image-painter: canvas not initialized");
            if (runner.sourcePixels == null)
                return Lv.Err("image-painter: source image not loaded");

            DataList frame = a[0];
            DataList origin = Lv.FrameOrigin(frame);
            DataList edge1 = Lv.FrameEdge1(frame);
            DataList edge2 = Lv.FrameEdge2(frame);

            double ox = Lv.VectX(origin);
            double oy = Lv.VectY(origin);
            double e1x = Lv.VectX(edge1);
            double e1y = Lv.VectY(edge1);
            double e2x = Lv.VectX(edge2);
            double e2y = Lv.VectY(edge2);

            int cw = runner.canvasW;
            int ch = runner.canvasH;
            Color32[] cPixels = runner.canvasPixels;
            Color32[] sPixels = runner.sourcePixels;
            int sw = runner.sourceW;
            int sh = runner.sourceH;

            // Compute step counts proportional to the actual frame edge lengths
            // in pixels, so small sub-frames don't waste time on 512x512 iterations.
            double e1len = System.Math.Sqrt(e1x * e1x + e1y * e1y);
            double e2len = System.Math.Sqrt(e2x * e2x + e2y * e2y);
            int stepsU = (int)System.Math.Ceiling(e1len);
            int stepsV = (int)System.Math.Ceiling(e2len);
            if (stepsU < 1) stepsU = 1;
            if (stepsV < 1) stepsV = 1;
            double invU = 1.0 / stepsU;
            double invV = 1.0 / stepsV;

            for (int vi = 0; vi < stepsV; vi++)
            {
                double v = vi * invV;
                for (int ui = 0; ui < stepsU; ui++)
                {
                    double u = ui * invU;

                    // Map (u,v) -> canvas pixel via frame-coord-map
                    int px = (int)(ox + u * e1x + v * e2x);
                    int py = (int)(oy + u * e1y + v * e2y);

                    if (px < 0 || px >= cw || py < 0 || py >= ch)
                        continue;

                    // Sample source image at (u, v)
                    int srcX = (int)(u * (sw - 1));
                    int srcY = (int)(v * (sh - 1)); // Y=0 is bottom in both Unity textures
                    if (srcX < 0) srcX = 0;
                    if (srcX >= sw) srcX = sw - 1;
                    if (srcY < 0) srcY = 0;
                    if (srcY >= sh) srcY = sh - 1;

                    cPixels[py * cw + px] = sPixels[srcY * sw + srcX];
                }
            }

            return Lv.Nil();
        }

        /// <summary>
        /// (image-painter-idx index frame) — blit source image at given index onto canvas.
        /// Same algorithm as BImagePainter but reads from runner.sourceImages[index].
        /// </summary>
        private static DataList BImagePainterIdx(DataList[] a, LispRunner runner = null)
        {
            if (a.Length != 2) return Lv.Err("image-painter-idx: requires 2 arguments (index frame)");
            if (!Lv.IsNumber(a[0])) return Lv.Err("image-painter-idx: first argument must be a number (index)");
            if (!Lv.IsFrame(a[1])) return Lv.Err("image-painter-idx: second argument must be a frame");
            if (runner == null || runner.canvasPixels == null)
                return Lv.Err("image-painter-idx: canvas not initialized");

            int idx = (int)Lv.ToNumber(a[0]);
            if (idx < 0 || idx >= runner.sourceImages.Length || runner.sourceImages[idx] == null)
                return Lv.Err("image-painter-idx: no image loaded at index " + idx);

            DataList frame = a[1];
            DataList origin = Lv.FrameOrigin(frame);
            DataList edge1 = Lv.FrameEdge1(frame);
            DataList edge2 = Lv.FrameEdge2(frame);

            double ox = Lv.VectX(origin);
            double oy = Lv.VectY(origin);
            double e1x = Lv.VectX(edge1);
            double e1y = Lv.VectY(edge1);
            double e2x = Lv.VectX(edge2);
            double e2y = Lv.VectY(edge2);

            int cw = runner.canvasW;
            int ch = runner.canvasH;
            Color32[] cPixels = runner.canvasPixels;
            Color32[] sPixels = runner.sourceImages[idx];
            int sw = runner.sourceWs[idx];
            int sh = runner.sourceHs[idx];

            // Compute step counts proportional to the actual frame edge lengths
            double e1len = System.Math.Sqrt(e1x * e1x + e1y * e1y);
            double e2len = System.Math.Sqrt(e2x * e2x + e2y * e2y);
            int stepsU = (int)System.Math.Ceiling(e1len);
            int stepsV = (int)System.Math.Ceiling(e2len);
            if (stepsU < 1) stepsU = 1;
            if (stepsV < 1) stepsV = 1;
            double invU = 1.0 / stepsU;
            double invV = 1.0 / stepsV;

            for (int vi = 0; vi < stepsV; vi++)
            {
                double v = vi * invV;
                for (int ui = 0; ui < stepsU; ui++)
                {
                    double u = ui * invU;

                    int px = (int)(ox + u * e1x + v * e2x);
                    int py = (int)(oy + u * e1y + v * e2y);

                    if (px < 0 || px >= cw || py < 0 || py >= ch)
                        continue;

                    int srcX = (int)(u * (sw - 1));
                    int srcY = (int)(v * (sh - 1));
                    if (srcX < 0) srcX = 0;
                    if (srcX >= sw) srcX = sw - 1;
                    if (srcY < 0) srcY = 0;
                    if (srcY >= sh) srcY = sh - 1;

                    cPixels[py * cw + px] = sPixels[srcY * sw + srcX];
                }
            }

            return Lv.Nil();
        }
        /// painter must be a Lisp procedure (closure) that takes a frame argument.
        /// Returns "rendered" string on success.
        /// </summary>
        private static DataList BRenderPainter(DataList[] a, LispRunner runner = null)
        {
            if (a.Length != 1) return Lv.Err("render-painter: requires 1 argument (painter procedure)");
            if (runner == null || runner.canvasPixels == null || runner.canvasTexture == null)
                return Lv.Err("render-painter: canvas not initialized");

            int cw = runner.canvasW;
            int ch = runner.canvasH;
            Color32[] pixels = runner.canvasPixels;

            // Clear canvas to black
            Color32 black = new Color32(0, 0, 0, 255);
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = black;

            // Create unit frame: origin=(0,0), edge1=(width,0), edge2=(0,height)
            DataList unitFrame = Lv.Frame(
                Lv.Vect(0, 0),
                Lv.Vect(cw, 0),
                Lv.Vect(0, ch)
            );

            // Call the painter with the unit frame
            DataList painter = a[0];
            DataList[] painterArgs = new DataList[] { unitFrame };
            DataList result = ApplyFunction(painter, painterArgs, runner);

            if (result != null && Lv.IsErr(result))
                return result;

            // Flush to GPU
            runner.canvasTexture.SetPixels32(pixels);
            runner.canvasTexture.Apply();

            Debug.Log("[PictureLanguage] Rendered to canvas " + cw + "x" + ch);
            return Lv.Str("rendered");
        }
    }
}
