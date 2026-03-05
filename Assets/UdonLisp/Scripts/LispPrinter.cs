using VRC.SDK3.Data;

namespace UdonLisp
{
    /// <summary>
    /// Converts LispValue (DataList) back to a human-readable string representation.
    /// All methods are static.
    /// </summary>
    public static class LispPrinter
    {
        [UdonSharp.RecursiveMethod]
        public static string Print(DataList value)
        {
            if (value == null) return "nil";

            string tag = Lv.Tag(value);

            switch (tag)
            {
                case Lv.TNil:
                    return "nil";
                case Lv.TBool:
                    return Lv.GetBool(value) ? "#t" : "#f";
                case Lv.TInt:
                    return Lv.GetInt(value).ToString();
                case Lv.TFloat:
                    return Lv.GetFloat(value).ToString();
                case Lv.TSym:
                    return Lv.GetSym(value);
                case Lv.TStr:
                    return "\"" + EscapeString(Lv.GetStr(value)) + "\"";
                case Lv.TChar:
                    return PrintChar(Lv.GetChar(value));
                case Lv.TList:
                    return PrintList(value);
                case Lv.TPair:
                    return PrintPair(value);
                case Lv.TVec:
                    return PrintVec(value);
                case Lv.TFn:
                    return "<lambda>";
                case Lv.TBuiltin:
                    return "<builtin:" + Lv.GetBuiltinName(value) + ">";
                case Lv.TPromise:
                    return Lv.IsPromiseForced(value)
                        ? "<promise:forced>"
                        : "<promise>";
                case Lv.TCont:
                    return "<continuation>";
                case Lv.TEscape:
                    return "<escape>";
                case Lv.TEof:
                    return "#<eof>";
                // Picture language types (SICP 2.2.4)
                case Lv.TVect:
                    return "#<vect " + Lv.VectX(value) + " " + Lv.VectY(value) + ">";
                case Lv.TFrame:
                    return "#<frame>";
                case Lv.TSegment:
                    return "#<segment>";
                case Lv.TErr:
                    return "[Error] " + Lv.ErrMsg(value);
                default:
                    return "???";
            }
        }

        /// <summary>
        /// Display-mode printing: strings without quotes, chars without #\ prefix.
        /// Used by (display ...).
        /// </summary>
        [UdonSharp.RecursiveMethod]
        public static string Display(DataList value)
        {
            if (value == null) return "nil";

            string tag = Lv.Tag(value);

            switch (tag)
            {
                case Lv.TStr:
                    return Lv.GetStr(value);
                case Lv.TChar:
                    return Lv.GetChar(value);
                default:
                    return Print(value);
            }
        }

        [UdonSharp.RecursiveMethod]
        private static string PrintList(DataList value)
        {
            int count = Lv.ListCount(value);
            if (count == 0) return "()";

            var result = "(";
            for (int i = 0; i < count; i++)
            {
                if (i > 0) result += " ";
                result += Print(Lv.ListGet(value, i));
            }
            result += ")";
            return result;
        }

        /// <summary>
        /// Print a pair (cons cell).
        /// If it is a proper list (chain ending in nil), print as (a b c).
        /// Otherwise print dotted pair notation: (a . b) or (a b . c).
        /// </summary>
        [UdonSharp.RecursiveMethod]
        private static string PrintPair(DataList value)
        {
            var result = "(";
            result += Print(Lv.Car(value));

            DataList rest = Lv.Cdr(value);
            while (true)
            {
                string t = Lv.Tag(rest);
                if (Lv.IsNil(rest))
                {
                    // End of proper list
                    break;
                }
                else if (t == Lv.TPair)
                {
                    // Another pair — continue the list
                    result += " " + Print(Lv.Car(rest));
                    rest = Lv.Cdr(rest);
                }
                else
                {
                    // Improper list — print dot notation
                    result += " . " + Print(rest);
                    break;
                }
            }

            result += ")";
            return result;
        }

        private static string PrintChar(string c)
        {
            if (c == " ") return "#\\space";
            if (c == "\n") return "#\\newline";
            if (c == "\t") return "#\\tab";
            return "#\\" + c;
        }

        [UdonSharp.RecursiveMethod]
        private static string PrintVec(DataList value)
        {
            int len = Lv.VecLen(value);
            var result = "#(";
            for (int i = 0; i < len; i++)
            {
                if (i > 0) result += " ";
                result += Print(Lv.VecRef(value, i));
            }
            result += ")";
            return result;
        }

        private static string EscapeString(string s)
        {
            if (s == null) return "";
            var result = "";
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') result += "\\\"";
                else if (c == '\\') result += "\\\\";
                else if (c == '\n') result += "\\n";
                else if (c == '\t') result += "\\t";
                else result += c;
            }
            return result;
        }
    }
}
