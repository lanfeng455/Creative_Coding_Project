// FlatJson.cs —— Prototype C：LLM 输出的严格「扁平 JSON 对象」解析
// 只接受 {"key":"value", ...} 这种形态：值为字符串、不允许数组/数字/嵌套。
// 唯一例外：unrecognized 字段允许 JSON 布尔字面量 true / false（存为 "true" / "false"），
// 因为拒绝形态天然是 {"unrecognized":true,"reason":"..."}。
// 多余字段、类型不合、重复键都会被拒绝，用来保证 Schema 校验是严格的。
// 同时负责从「```json 代码块 / 前后废话」里把 JSON 对象抠出来。

using System.Collections.Generic;

namespace PrototypeC
{
    internal static class FlatJson
    {
        /// <summary>从任意文本里提取第一个 '{' 到最后一个 '}' 之间的内容。</summary>
        public static bool TryExtractObject(string text, out string json, out string error)
        {
            json = null;
            error = null;

            if (string.IsNullOrWhiteSpace(text))
            {
                error = "响应为空";
                return false;
            }

            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                error = "响应中没有 JSON 对象";
                return false;
            }

            json = text.Substring(start, end - start + 1);
            return true;
        }

        /// <summary>严格解析扁平 JSON 对象；任何非字符串值、重复键、语法问题都算失败。</summary>
        public static bool TryParseFlatObject(string json, out Dictionary<string, string> values, out string error)
        {
            values = new Dictionary<string, string>();
            error = null;

            int i = 0;
            SkipWhitespace(json, ref i);

            if (i >= json.Length || json[i] != '{')
            {
                error = "JSON 顶层必须是对象";
                return false;
            }

            i++;
            SkipWhitespace(json, ref i);

            if (i < json.Length && json[i] == '}')
            {
                i++;
            }
            else
            {
                while (true)
                {
                    SkipWhitespace(json, ref i);
                    if (i >= json.Length || json[i] != '"')
                    {
                        error = $"第 {i} 个字符处缺少字段名";
                        return false;
                    }

                    if (!ReadString(json, ref i, out string key, out error)) return false;

                    SkipWhitespace(json, ref i);
                    if (i >= json.Length || json[i] != ':')
                    {
                        error = $"字段 {key} 后缺少冒号";
                        return false;
                    }

                    i++;
                    SkipWhitespace(json, ref i);
                    if (i >= json.Length)
                    {
                        error = $"字段 {key} 缺少值";
                        return false;
                    }

                    string value;
                    if (json[i] == '"')
                    {
                        if (!ReadString(json, ref i, out value, out error)) return false;
                    }
                    else if (key == "unrecognized")
                    {
                        // 拒绝形态唯一允许的布尔字面量：{"unrecognized":true,"reason":"..."}
                        if (!ReadBooleanLiteral(json, ref i, out value, out error)) return false;
                    }
                    else
                    {
                        error = $"字段 {key} 的值必须是字符串";
                        return false;
                    }

                    if (values.ContainsKey(key))
                    {
                        error = $"字段 {key} 重复出现";
                        return false;
                    }

                    values[key] = value;

                    SkipWhitespace(json, ref i);
                    if (i >= json.Length)
                    {
                        error = "JSON 未正常结束";
                        return false;
                    }

                    if (json[i] == ',')
                    {
                        i++;
                        continue;
                    }

                    if (json[i] == '}')
                    {
                        i++;
                        break;
                    }

                    error = $"第 {i} 个字符处应为 ',' 或 '}}'";
                    return false;
                }
            }

            SkipWhitespace(json, ref i);
            if (i != json.Length)
            {
                error = "JSON 对象之后还有多余内容";
                return false;
            }

            return true;
        }

        private static void SkipWhitespace(string text, ref int i)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        }

        private static bool ReadBooleanLiteral(string text, ref int i, out string value, out string error)
        {
            value = null;
            error = null;

            if (Matches(text, i, "true"))
            {
                value = "true";
                i += 4;
                return true;
            }

            if (Matches(text, i, "false"))
            {
                value = "false";
                i += 5;
                return true;
            }

            error = "unrecognized 的值必须是 true 或 false";
            return false;
        }

        private static bool Matches(string text, int index, string literal)
        {
            if (index + literal.Length > text.Length) return false;
            for (int k = 0; k < literal.Length; k++)
            {
                if (text[index + k] != literal[k]) return false;
            }

            // 字面量之后必须是分隔符，避免把 truex 当成 true
            int next = index + literal.Length;
            if (next < text.Length)
            {
                char c = text[next];
                if (c != ',' && c != '}' && !char.IsWhiteSpace(c)) return false;
            }

            return true;
        }

        private static bool ReadString(string text, ref int i, out string value, out string error)
        {
            value = null;
            error = null;

            if (i >= text.Length || text[i] != '"')
            {
                error = "缺少字符串起始引号";
                return false;
            }

            i++;
            var sb = new System.Text.StringBuilder();

            while (i < text.Length)
            {
                char c = text[i];

                if (c == '"')
                {
                    i++;
                    value = sb.ToString();
                    return true;
                }

                if (c == '\\')
                {
                    i++;
                    if (i >= text.Length)
                    {
                        error = "字符串转义不完整";
                        return false;
                    }

                    char esc = text[i];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 >= text.Length ||
                                !int.TryParse(text.Substring(i + 1, 4),
                                    System.Globalization.NumberStyles.HexNumber, null, out int code))
                            {
                                error = "\\u 转义不合法";
                                return false;
                            }
                            sb.Append((char)code);
                            i += 4;
                            break;
                        default:
                            error = $"不支持的转义 \\{esc}";
                            return false;
                    }

                    i++;
                    continue;
                }

                sb.Append(c);
                i++;
            }

            error = "字符串没有闭合";
            return false;
        }
    }
}
