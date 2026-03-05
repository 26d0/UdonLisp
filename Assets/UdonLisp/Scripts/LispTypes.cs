using VRC.SDK3.Data;

namespace UdonLisp
{
    /// <summary>
    /// Static helper methods for creating and inspecting Lisp values
    /// represented as DataList (tagged tuples).
    ///
    /// Encoding convention:
    ///   Each LispValue is a DataList whose first element is a type tag string.
    ///
    ///   ["nil"]                          -- nil
    ///   ["bool", true/false]             -- boolean
    ///   ["int", (double)n]               -- integer (stored as double in DataToken)
    ///   ["float", (double)f]             -- float
    ///   ["sym", "name"]                  -- symbol
    ///   ["str", "text"]                  -- string
    ///   ["list", elem0, elem1, ...]      -- list (elements are LispValues)
    ///   ["fn", paramsDataList, body, envRef]  -- user function (closure)
    ///   ["builtin", "name"]              -- built-in function
    ///   ["err", "message"]               -- error
    ///
    /// Token encoding (from Tokenizer):
    ///   DataDictionary { "t": tokenType, "v": rawValue }
    ///
    /// Environment encoding:
    ///   DataDictionary { "__parent": parentEnvOrNull, ...bindings... }
    ///   Where each binding key is a string name and value is a LispValue (DataList).
    /// </summary>
    public static class Lv
    {
        // ---- Type tag constants ----
        public const string TNil = "nil";
        public const string TBool = "bool";
        public const string TInt = "int";
        public const string TFloat = "float";
        public const string TSym = "sym";
        public const string TStr = "str";
        public const string TList = "list";
        public const string TPair = "pair";
        public const string TFn = "fn";
        public const string TBuiltin = "builtin";
        public const string TErr = "err";
        public const string TChar = "char";
        public const string TVec = "vec";
        public const string TEnv = "env";
        public const string TVals = "vals";
        public const string TPromise = "promise";
        public const string TCont = "cont";
        public const string TEscape = "escape";
        public const string TEof = "eof";
        // Picture language types (SICP 2.2.4)
        public const string TVect = "vect";
        public const string TFrame = "frame";
        public const string TSegment = "segment";

        // ---- Factory methods ----

        public static DataList Nil()
        {
            var v = new DataList();
            v.Add(TNil);
            return v;
        }

        public static DataList Eof()
        {
            var v = new DataList();
            v.Add(TEof);
            return v;
        }

        public static DataList Bool(bool b)
        {
            var v = new DataList();
            v.Add(TBool);
            v.Add(b);
            return v;
        }

        public static DataList Int(int n)
        {
            var v = new DataList();
            v.Add(TInt);
            v.Add((double)n);
            return v;
        }

        public static DataList Float(double f)
        {
            var v = new DataList();
            v.Add(TFloat);
            v.Add(f);
            return v;
        }

        public static DataList Sym(string name)
        {
            var v = new DataList();
            v.Add(TSym);
            v.Add(name);
            return v;
        }

        public static DataList Char(string c)
        {
            var v = new DataList();
            v.Add(TChar);
            v.Add(c);
            return v;
        }

        public static string GetChar(DataList val)
        {
            return val[1].String;
        }

        /// <summary>
        /// Create a vector value. Elements are DataTokens wrapping DataList (LispValues).
        /// Encoded as ["vec", elem0, elem1, ...].
        /// </summary>
        public static DataList Vec(DataList[] elems)
        {
            var v = new DataList();
            v.Add(TVec);
            for (int i = 0; i < elems.Length; i++)
                v.Add(new DataToken(elems[i]));
            return v;
        }

        /// <summary>
        /// Create a vector of given size filled with a default value.
        /// </summary>
        public static DataList VecMake(int size, DataList fill)
        {
            var v = new DataList();
            v.Add(TVec);
            for (int i = 0; i < size; i++)
                v.Add(new DataToken(fill));
            return v;
        }

        public static int VecLen(DataList vec)
        {
            return vec.Count - 1;
        }

        public static DataList VecRef(DataList vec, int k)
        {
            return vec[k + 1].DataList;
        }

        public static void VecSet(DataList vec, int k, DataList val)
        {
            vec[k + 1] = new DataToken(val);
        }

        /// <summary>
        /// Wrap an environment DataDictionary as a LispValue.
        /// Encoded as ["env", envDict].
        /// </summary>
        public static DataList Env(DataDictionary envDict)
        {
            var v = new DataList();
            v.Add(TEnv);
            v.Add(new DataToken(envDict));
            return v;
        }

        public static DataDictionary GetEnv(DataList val)
        {
            return val[1].DataDictionary;
        }

        /// <summary>
        /// Create a multiple-values wrapper. Encoded as ["vals", v0, v1, ...].
        /// </summary>
        public static DataList Vals(DataList[] vals)
        {
            var v = new DataList();
            v.Add(TVals);
            for (int i = 0; i < vals.Length; i++)
                v.Add(new DataToken(vals[i]));
            return v;
        }

        public static int ValsCount(DataList vals)
        {
            return vals.Count - 1;
        }

        public static DataList ValsRef(DataList vals, int k)
        {
            return vals[k + 1].DataList;
        }

        // ---- Promise type ----
        // Encoding: ["promise", expr, envDict, forced(bool), cachedResult]

        /// <summary>
        /// Create an unforced promise wrapping an unevaluated expression and its environment.
        /// </summary>
        public static DataList Promise(DataList expr, DataDictionary env)
        {
            var v = new DataList();
            v.Add(TPromise);
            v.Add(new DataToken(expr));
            v.Add(new DataToken(env));
            v.Add(false);
            v.Add(new DataToken(Nil()));
            return v;
        }

        /// <summary>
        /// Create an already-forced promise wrapping a computed value.
        /// Used by make-promise.
        /// </summary>
        public static DataList PromiseForced(DataList result)
        {
            var v = new DataList();
            v.Add(TPromise);
            v.Add(new DataToken(Nil()));    // expr (unused)
            v.Add(new DataToken(new DataDictionary())); // env (unused)
            v.Add(true);
            v.Add(new DataToken(result));
            return v;
        }

        public static bool IsPromiseForced(DataList p)
        {
            return p[3].Boolean;
        }

        public static DataList PromiseExpr(DataList p)
        {
            return p[1].DataList;
        }

        public static DataDictionary PromiseEnv(DataList p)
        {
            return p[2].DataDictionary;
        }

        public static DataList PromiseResult(DataList p)
        {
            return p[4].DataList;
        }

        /// <summary>
        /// Mutate a promise to mark it as forced and cache the result.
        /// </summary>
        public static void PromiseSetForced(DataList p, DataList result)
        {
            p.SetValue(3, new DataToken(true));
            p.SetValue(4, new DataToken(result));
        }

        // ---- Continuation / escape types (escape-only call/cc) ----
        // Continuation: ["cont", sentinelDataList]
        // Escape signal: ["escape", sentinelDataList, value]

        /// <summary>
        /// Create a continuation object. The sentinel is a unique DataList
        /// used to match escape signals to their corresponding call/cc.
        /// </summary>
        public static DataList Cont(DataList sentinel)
        {
            var v = new DataList();
            v.Add(TCont);
            v.Add(new DataToken(sentinel));
            return v;
        }

        /// <summary>Get the sentinel from a continuation.</summary>
        public static DataList ContSentinel(DataList cont)
        {
            return cont[1].DataList;
        }

        /// <summary>
        /// Create an escape signal. When a continuation is invoked,
        /// this value propagates up the call stack until caught by call/cc.
        /// </summary>
        public static DataList Escape(DataList sentinel, DataList value)
        {
            var v = new DataList();
            v.Add(TEscape);
            v.Add(new DataToken(sentinel));
            v.Add(new DataToken(value));
            return v;
        }

        public static DataList EscapeSentinel(DataList esc)
        {
            return esc[1].DataList;
        }

        public static DataList EscapeValue(DataList esc)
        {
            return esc[2].DataList;
        }

        public static bool IsEscape(DataList val)
        {
            return Tag(val) == TEscape;
        }

        /// <summary>
        /// Check if a value should propagate up the call stack immediately
        /// (errors and escape signals both need this behavior).
        /// </summary>
        public static bool ShouldReturn(DataList val)
        {
            string t = Tag(val);
            return t == TErr || t == TEscape;
        }

        public static DataList Str(string text)
        {
            var v = new DataList();
            v.Add(TStr);
            v.Add(text);
            return v;
        }

        /// <summary>
        /// Create a list value. Items are DataTokens wrapping DataList (LispValues).
        /// </summary>
        public static DataList List(DataToken[] items)
        {
            var v = new DataList();
            v.Add(TList);
            for (int i = 0; i < items.Length; i++)
                v.Add(items[i]);
            return v;
        }

        public static DataList ListEmpty()
        {
            var v = new DataList();
            v.Add(TList);
            return v;
        }

        /// <summary>
        /// Create a cons pair: ["pair", car, cdr]
        /// </summary>
        public static DataList Pair(DataList car, DataList cdr)
        {
            var v = new DataList();
            v.Add(TPair);
            v.Add(new DataToken(car));
            v.Add(new DataToken(cdr));
            return v;
        }

        /// <summary>
        /// Get the car of a pair.
        /// </summary>
        public static DataList Car(DataList pair)
        {
            return pair[1].DataList;
        }

        /// <summary>
        /// Get the cdr of a pair.
        /// </summary>
        public static DataList Cdr(DataList pair)
        {
            return pair[2].DataList;
        }

        /// <summary>
        /// Set the car of a pair (mutation).
        /// </summary>
        public static void SetCar(DataList pair, DataList val)
        {
            pair.SetValue(1, new DataToken(val));
        }

        /// <summary>
        /// Set the cdr of a pair (mutation).
        /// </summary>
        public static void SetCdr(DataList pair, DataList val)
        {
            pair.SetValue(2, new DataToken(val));
        }

        public static bool IsPair(DataList val)
        {
            return Tag(val) == TPair;
        }

        /// <summary>
        /// Check if a value is a proper list: either
        /// nil, empty list, or a chain of pairs ending in nil/empty-list.
        /// Iterative implementation.
        /// </summary>
        public static bool IsProperList(DataList val)
        {
            DataList cur = val;
            while (true)
            {
                string t = Tag(cur);
                if (t == TNil) return true;
                if (t == TList) return true;
                if (t != TPair) return false;
                cur = Cdr(cur);
            }
        }

        /// <summary>
        /// Count the length of a proper list (pair chain).
        /// Returns -1 for improper lists.
        /// Iterative implementation.
        /// </summary>
        public static int PairListCount(DataList val)
        {
            int count = 0;
            DataList cur = val;
            while (true)
            {
                string t = Tag(cur);
                if (t == TNil || (t == TList && ListCount(cur) == 0)) return count;
                if (t != TPair) return -1;
                count++;
                cur = Cdr(cur);
            }
        }

        /// <summary>
        /// Build a proper list from an array of elements as a pair chain
        /// terminated by nil.
        /// </summary>
        public static DataList PairList(DataList[] items)
        {
            DataList result = Nil();
            for (int i = items.Length - 1; i >= 0; i--)
                result = Pair(items[i], result);
            return result;
        }

        public static DataList Fn(DataList paramNames, DataList body, DataDictionary closure)
        {
            var v = new DataList();
            v.Add(TFn);
            v.Add(new DataToken(paramNames));
            v.Add(new DataToken(body));
            v.Add(new DataToken(closure));
            return v;
        }

        public static DataList Builtin(string name)
        {
            var v = new DataList();
            v.Add(TBuiltin);
            v.Add(name);
            return v;
        }

        public static DataList Err(string message)
        {
            var v = new DataList();
            v.Add(TErr);
            v.Add(message);
            return v;
        }

        // ---- Accessors ----

        public static string Tag(DataList val)
        {
            if (val == null || val.Count == 0) return TNil;
            return val[0].String;
        }

        public static bool IsErr(DataList val)
        {
            return Tag(val) == TErr;
        }

        public static string ErrMsg(DataList val)
        {
            if (val.Count < 2) return "unknown error";
            return val[1].String;
        }

        public static bool GetBool(DataList val)
        {
            return val[1].Boolean;
        }

        public static int GetInt(DataList val)
        {
            return (int)val[1].Number;
        }

        public static double GetFloat(DataList val)
        {
            return val[1].Number;
        }

        public static string GetStr(DataList val)
        {
            return val[1].String;
        }

        public static string GetSym(DataList val)
        {
            return val[1].String;
        }

        public static string GetBuiltinName(DataList val)
        {
            return val[1].String;
        }

        /// <summary>Number of list elements (excluding the type tag). Works on TList only.</summary>
        public static int ListCount(DataList val)
        {
            return val.Count - 1;
        }

        /// <summary>Get the i-th element of a list value (0-indexed, skipping tag). Works on TList only.</summary>
        public static DataList ListGet(DataList val, int index)
        {
            return val[index + 1].DataList;
        }

        /// <summary>
        /// Unified count: works on both TList (legacy) and TPair chains.
        /// For nil, returns 0. For improper lists, counts elements before the final non-pair cdr.
        /// Iterative implementation to avoid Udon recursive-method stack limits.
        /// </summary>
        public static int Count(DataList val)
        {
            string t = Tag(val);
            if (t == TNil) return 0;
            if (t == TList) return ListCount(val);
            if (t != TPair) return 0;
            // Iterative pair-chain walk
            int count = 0;
            DataList cur = val;
            while (Tag(cur) == TPair)
            {
                count++;
                cur = Cdr(cur);
            }
            return count;
        }

        /// <summary>
        /// Unified indexed access: works on both TList (legacy) and TPair chains.
        /// Iterative implementation to avoid Udon recursive-method stack limits.
        /// </summary>
        public static DataList GetAt(DataList val, int index)
        {
            string t = Tag(val);
            if (t == TList) return ListGet(val, index);
            if (t != TPair) return Nil();
            // Iterative pair-chain walk
            DataList cur = val;
            for (int i = 0; i < index; i++)
            {
                if (Tag(cur) != TPair) return Nil();
                cur = Cdr(cur);
            }
            if (Tag(cur) == TPair) return Car(cur);
            return Nil();
        }

        /// <summary>
        /// Check if a value is nil or empty list (the list terminator).
        /// </summary>
        public static bool IsNil(DataList val)
        {
            string t = Tag(val);
            if (t == TNil) return true;
            if (t == TList && ListCount(val) == 0) return true;
            return false;
        }

        /// <summary>Get function param names DataList.</summary>
        public static DataList FnParams(DataList val)
        {
            return val[1].DataList;
        }

        /// <summary>Get function body (LispValue).</summary>
        public static DataList FnBody(DataList val)
        {
            return val[2].DataList;
        }

        /// <summary>Get function closure environment.</summary>
        public static DataDictionary FnClosure(DataList val)
        {
            return val[3].DataDictionary;
        }

        /// <summary>
        /// Convert a numeric LispValue to double regardless of int/float tag.
        /// </summary>
        public static double ToNumber(DataList val)
        {
            return val[1].Number;
        }

        public static bool IsNumber(DataList val)
        {
            var t = Tag(val);
            return t == TInt || t == TFloat;
        }

        /// <summary>
        /// Truthy: everything except nil and #f is truthy.
        /// </summary>
        public static bool IsTruthy(DataList val)
        {
            var t = Tag(val);
            if (t == TNil) return false;
            if (t == TBool) return GetBool(val);
            return true;
        }

        /// <summary>
        /// Returns true if any of the numeric values has a float tag.
        /// </summary>
        public static bool AnyFloat(DataList[] args)
        {
            for (int i = 0; i < args.Length; i++)
                if (Tag(args[i]) == TFloat) return true;
            return false;
        }

        // ---- Picture language types (SICP 2.2.4) ----

        /// <summary>Creates a 2D vector: ["vect", (double)x, (double)y]</summary>
        public static DataList Vect(double x, double y)
        {
            var v = new DataList();
            v.Add(TVect);
            v.Add(x);
            v.Add(y);
            return v;
        }

        public static bool IsVect(DataList v)
        {
            return v != null && Tag(v) == TVect;
        }

        public static double VectX(DataList v) { return v[1].Double; }
        public static double VectY(DataList v) { return v[2].Double; }

        /// <summary>Creates a frame: ["frame", origin, edge1, edge2] (each a vect)</summary>
        public static DataList Frame(DataList origin, DataList edge1, DataList edge2)
        {
            var f = new DataList();
            f.Add(TFrame);
            f.Add(new DataToken(origin));
            f.Add(new DataToken(edge1));
            f.Add(new DataToken(edge2));
            return f;
        }

        public static bool IsFrame(DataList v)
        {
            return v != null && Tag(v) == TFrame;
        }

        public static DataList FrameOrigin(DataList f) { return f[1].DataList; }
        public static DataList FrameEdge1(DataList f) { return f[2].DataList; }
        public static DataList FrameEdge2(DataList f) { return f[3].DataList; }

        /// <summary>Creates a segment: ["segment", startVect, endVect]</summary>
        public static DataList Segment(DataList start, DataList end)
        {
            var s = new DataList();
            s.Add(TSegment);
            s.Add(new DataToken(start));
            s.Add(new DataToken(end));
            return s;
        }

        public static bool IsSegment(DataList v)
        {
            return v != null && Tag(v) == TSegment;
        }

        public static DataList SegmentStart(DataList s) { return s[1].DataList; }
        public static DataList SegmentEnd(DataList s) { return s[2].DataList; }
    }
}
