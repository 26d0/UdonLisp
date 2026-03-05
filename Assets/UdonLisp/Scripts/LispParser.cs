using UdonSharp;
using VRC.SDK3.Data;

namespace UdonLisp
{
    /// <summary>
    /// Parser for Lisp tokens.
    /// Converts a flat DataList of token dictionaries into a nested LispValue (DataList AST).
    ///
    /// All methods are static. Parser state (position) is passed via int[] wrapper
    /// since UdonSharp doesn't support ref/out on user methods reliably.
    /// We use a DataList with a single int element as the mutable position counter.
    /// </summary>
    public static class LispParser
    {
        /// <summary>
        /// Parse a token list into a single LispValue (AST).
        /// Returns null-tagged nil on empty input.
        /// </summary>
        public static DataList Parse(DataList tokens)
        {
            if (tokens == null || tokens.Count == 0)
                return Lv.Nil();

            int[] pos = new int[] { 0 };
            return ParseExpr(tokens, pos);
        }

        /// <summary>
        /// Parse all top-level expressions.
        /// If multiple, wraps in (begin expr1 expr2 ...).
        /// </summary>
        public static DataList ParseAll(DataList tokens)
        {
            if (tokens == null || tokens.Count == 0)
                return Lv.Nil();

            int[] pos = new int[] { 0 };
            var exprs = new DataList();

            while (pos[0] < tokens.Count)
            {
                var expr = ParseExpr(tokens, pos);
                if (expr != null)
                    exprs.Add(new DataToken(expr));
            }

            if (exprs.Count == 0) return Lv.Nil();
            if (exprs.Count == 1) return exprs[0].DataList;

            // Wrap in (begin ...)
            var items = new DataToken[exprs.Count + 1];
            items[0] = new DataToken(Lv.Sym("begin"));
            for (int i = 0; i < exprs.Count; i++)
                items[i + 1] = exprs[i];
            return Lv.List(items);
        }

        [RecursiveMethod]
        private static DataList ParseExpr(DataList tokens, int[] pos)
        {
            if (pos[0] >= tokens.Count) return Lv.Nil();

            var tok = tokens[pos[0]];
            string tt = LispTokenizer.TokenType(tok);
            string tv = LispTokenizer.TokenValue(tok);

            // Quote shorthand: 'x => (quote x)
            if (tt == "qt")
            {
                pos[0]++;
                var quoted = ParseExpr(tokens, pos);
                return Lv.List(new DataToken[]
                {
                    new DataToken(Lv.Sym("quote")),
                    new DataToken(quoted),
                });
            }

            // Quasiquote shorthand: `x => (quasiquote x)
            if (tt == "qq")
            {
                pos[0]++;
                var quoted = ParseExpr(tokens, pos);
                return Lv.List(new DataToken[]
                {
                    new DataToken(Lv.Sym("quasiquote")),
                    new DataToken(quoted),
                });
            }

            // Unquote shorthand: ,x => (unquote x)
            if (tt == "uq")
            {
                pos[0]++;
                var quoted = ParseExpr(tokens, pos);
                return Lv.List(new DataToken[]
                {
                    new DataToken(Lv.Sym("unquote")),
                    new DataToken(quoted),
                });
            }

            // Unquote-splicing shorthand: ,@x => (unquote-splicing x)
            if (tt == "uqs")
            {
                pos[0]++;
                var quoted = ParseExpr(tokens, pos);
                return Lv.List(new DataToken[]
                {
                    new DataToken(Lv.Sym("unquote-splicing")),
                    new DataToken(quoted),
                });
            }

            // Vector literal: #(expr ...) => vector
            if (tt == "vecopen")
            {
                pos[0]++; // skip #(
                var items = new DataList();
                while (pos[0] < tokens.Count && LispTokenizer.TokenType(tokens[pos[0]]) != "rp")
                {
                    var item = ParseExpr(tokens, pos);
                    items.Add(new DataToken(item));
                }
                if (pos[0] < tokens.Count) pos[0]++; // skip )
                DataList[] elems = new DataList[items.Count];
                for (int i = 0; i < items.Count; i++)
                    elems[i] = items[i].DataList;
                return Lv.Vec(elems);
            }

            // List: ( expr* ) or dotted pair ( expr . expr )
            if (tt == "lp")
            {
                pos[0]++; // skip (
                var items = new DataList();
                bool dotted = false;
                DataList dotCdr = null;

                while (pos[0] < tokens.Count && LispTokenizer.TokenType(tokens[pos[0]]) != "rp")
                {
                    // Check for dot notation
                    if (LispTokenizer.TokenType(tokens[pos[0]]) == "dot")
                    {
                        pos[0]++; // skip .
                        if (items.Count == 0)
                            return Lv.Err("unexpected '.' at start of list");
                        dotCdr = ParseExpr(tokens, pos);
                        dotted = true;
                        break;
                    }
                    var item = ParseExpr(tokens, pos);
                    items.Add(new DataToken(item));
                }
                if (pos[0] < tokens.Count) pos[0]++; // skip )

                if (dotted)
                {
                    // Build pair chain: (a b . c) => (pair a (pair b c))
                    DataList result = dotCdr;
                    for (int i = items.Count - 1; i >= 0; i--)
                        result = Lv.Pair(items[i].DataList, result);
                    return result;
                }

                // Regular list: build as pair chain terminated by nil
                // (a b c) => (pair a (pair b (pair c nil)))
                if (items.Count == 0)
                    return Lv.Nil();

                DataList lst = Lv.Nil();
                for (int i = items.Count - 1; i >= 0; i--)
                    lst = Lv.Pair(items[i].DataList, lst);
                return lst;
            }

            // Unexpected closing paren
            if (tt == "rp")
            {
                pos[0]++;
                return Lv.Err("unexpected ')'");
            }

            // Atoms
            pos[0]++;
            return ParseAtom(tt, tv);
        }

        private static DataList ParseAtom(string tt, string tv)
        {
            if (tt == "int")
            {
                // Use double.TryParse since DataToken stores numbers as double
                double d;
                if (double.TryParse(tv, out d))
                    return Lv.Int((int)d);
                return Lv.Err("invalid integer: " + tv);
            }
            if (tt == "float")
            {
                double d;
                if (double.TryParse(tv, out d))
                    return Lv.Float(d);
                return Lv.Err("invalid float: " + tv);
            }
            if (tt == "str")
                return Lv.Str(tv);
            if (tt == "bool")
                return Lv.Bool(tv == "#t");
            if (tt == "char")
                return Lv.Char(tv);
            if (tt == "sym")
            {
                if (tv == "nil") return Lv.Nil();
                return Lv.Sym(tv);
            }
            return Lv.Err("unknown token type: " + tt);
        }
    }
}
