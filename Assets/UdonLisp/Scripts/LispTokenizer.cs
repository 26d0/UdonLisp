using VRC.SDK3.Data;

namespace UdonLisp
{
    /// <summary>
    /// Tokenizer (lexer) for Lisp source code.
    /// All methods are static -- no instance fields needed.
    ///
    /// Output: DataList of token DataDictionaries.
    /// Each token: DataDictionary { "t": tokenType, "v": rawValue }
    ///
     /// Token types: "lp", "rp", "qt", "qq", "uq", "uqs", "dot", "int", "float", "str", "bool", "char", "vecopen", "sym"
    /// </summary>
    public static class LispTokenizer
    {
        public static DataList Tokenize(string source)
        {
            var tokens = new DataList();
            int i = 0;
            int len = source.Length;

            while (i < len)
            {
                char c = source[i];

                // Skip whitespace
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                {
                    i++;
                    continue;
                }

                // Line comment
                if (c == ';')
                {
                    while (i < len && source[i] != '\n')
                        i++;
                    continue;
                }

                // Parentheses
                if (c == '(')
                {
                    tokens.Add(MakeToken("lp", "("));
                    i++;
                    continue;
                }
                if (c == ')')
                {
                    tokens.Add(MakeToken("rp", ")"));
                    i++;
                    continue;
                }

                // Quote shorthand
                if (c == '\'')
                {
                    tokens.Add(MakeToken("qt", "'"));
                    i++;
                    continue;
                }

                // Quasiquote
                if (c == '`')
                {
                    tokens.Add(MakeToken("qq", "`"));
                    i++;
                    continue;
                }

                // Unquote / unquote-splicing
                if (c == ',')
                {
                    if (i + 1 < len && source[i + 1] == '@')
                    {
                        tokens.Add(MakeToken("uqs", ",@"));
                        i += 2;
                    }
                    else
                    {
                        tokens.Add(MakeToken("uq", ","));
                        i++;
                    }
                    continue;
                }

                // String literal
                if (c == '"')
                {
                    i++; // skip opening "
                    var sb = "";
                    while (i < len && source[i] != '"')
                    {
                        if (source[i] == '\\' && i + 1 < len)
                        {
                            char next = source[i + 1];
                            if (next == '"' || next == '\\')
                            {
                                sb += next;
                                i += 2;
                                continue;
                            }
                            if (next == 'n') { sb += '\n'; i += 2; continue; }
                            if (next == 't') { sb += '\t'; i += 2; continue; }
                        }
                        sb += source[i];
                        i++;
                    }
                    if (i < len) i++; // skip closing "
                    tokens.Add(MakeToken("str", sb));
                    continue;
                }

                // Boolean literals #t, #f and character literals #\x
                if (c == '#' && i + 1 < len)
                {
                    char next = source[i + 1];
                    if (next == 't') { tokens.Add(MakeToken("bool", "#t")); i += 2; continue; }
                    if (next == 'f') { tokens.Add(MakeToken("bool", "#f")); i += 2; continue; }
                    if (next == '(') { tokens.Add(MakeToken("vecopen", "#(")); i += 2; continue; }
                    if (next == '\\' && i + 2 < len)
                    {
                        // Character literal: #\a, #\space, #\newline, #\tab
                        i += 2; // skip #\
                        int nameStart = i;
                        // Read the character name (letters only for named chars)
                        if (i < len && IsLetter(source[i]))
                        {
                            while (i < len && IsLetter(source[i]))
                                i++;
                            string charName = source.Substring(nameStart, i - nameStart);
                            if (charName.Length == 1)
                            {
                                tokens.Add(MakeToken("char", charName));
                            }
                            else if (charName == "space")
                            {
                                tokens.Add(MakeToken("char", " "));
                            }
                            else if (charName == "newline")
                            {
                                tokens.Add(MakeToken("char", "\n"));
                            }
                            else if (charName == "tab")
                            {
                                tokens.Add(MakeToken("char", "\t"));
                            }
                            else
                            {
                                tokens.Add(MakeToken("char", charName));
                            }
                        }
                        else if (i < len)
                        {
                            // Single non-letter char like #\( or #\1
                            tokens.Add(MakeToken("char", source[i].ToString()));
                            i++;
                        }
                        continue;
                    }
                }

                // Number or negative number
                if (IsDigit(c) || (c == '-' && i + 1 < len && IsDigit(source[i + 1])))
                {
                    int start = i;
                    if (c == '-') i++;
                    bool isFloat = false;
                    while (i < len && (IsDigit(source[i]) || source[i] == '.'))
                    {
                        if (source[i] == '.') isFloat = true;
                        i++;
                    }
                    if (i > start + 1 || c != '-')
                    {
                        string numStr = source.Substring(start, i - start);
                        tokens.Add(MakeToken(isFloat ? "float" : "int", numStr));
                        continue;
                    }
                    i = start; // fall through to symbol for bare "-"
                }

                // Symbol
                if (IsSymbolStart(c))
                {
                    int start = i;
                    while (i < len && IsSymbolChar(source[i]))
                        i++;
                    string sym = source.Substring(start, i - start);
                    if (sym == ".")
                        tokens.Add(MakeToken("dot", "."));
                    else
                        tokens.Add(MakeToken("sym", sym));
                    continue;
                }

                // Unknown character -- skip
                i++;
            }

            return tokens;
        }

        private static DataToken MakeToken(string type, string value)
        {
            var d = new DataDictionary();
            d.SetValue("t", type);
            d.SetValue("v", value);
            return new DataToken(d);
        }

        // ---- Token access helpers (for use in Parser) ----

        public static string TokenType(DataToken tok)
        {
            return tok.DataDictionary["t"].String;
        }

        public static string TokenValue(DataToken tok)
        {
            return tok.DataDictionary["v"].String;
        }

        private static bool IsDigit(char c) { return c >= '0' && c <= '9'; }
        private static bool IsLetter(char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'); }

        private static bool IsSymbolStart(char c)
        {
            return !IsDigit(c) && c != '(' && c != ')' && c != '"' && c != '\''
                && c != '`' && c != ','
                && c != ';' && c != '#' && c != ' ' && c != '\t' && c != '\n' && c != '\r';
        }

        private static bool IsSymbolChar(char c)
        {
            return c != '(' && c != ')' && c != '"' && c != '\''
                && c != '`' && c != ','
                && c != ';' && c != ' ' && c != '\t' && c != '\n' && c != '\r';
        }
    }
}
