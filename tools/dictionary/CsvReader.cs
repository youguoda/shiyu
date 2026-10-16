using System.Text;

namespace DictionaryBuilder;

/// <summary>
/// 最小的 RFC 4180 CSV 读取器：逗号分隔，双引号包裹的字段里可以有逗号、换行，
/// 字段里的引号写成两个引号。ECDICT 的 ecdict.csv 就是这种格式（行尾 CRLF，
/// 字段内的换行是字面的两个字符 "\n"，不是真换行，但这里两种都能处理）。
/// 逐字符流式读取，不把整个文件载入内存。
/// </summary>
internal static class CsvReader
{
    /// <summary>逐行产出字段列表。每次产出的 List 都是新的，调用方可以放心保留。</summary>
    public static IEnumerable<List<string>> ReadRows(TextReader reader)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;     // 当前是否处在引号包裹的字段里
        var rowHasContent = false; // 本行是否已经读到过任何字符（用来忽略空行）

        int c;
        while ((c = reader.Read()) != -1)
        {
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); field.Append('"'); } // "" -> "
                    else inQuotes = false;                                           // 引号字段结束
                }
                else field.Append((char)c);
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    rowHasContent = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    rowHasContent = true;
                    break;
                case '\r':
                    break; // 忽略 CR，由随后的 LF 结束这一行
                case '\n':
                    if (rowHasContent)
                    {
                        row.Add(field.ToString());
                        yield return row;
                        row = new List<string>();
                    }
                    field.Clear();
                    rowHasContent = false;
                    break;
                default:
                    field.Append((char)c);
                    rowHasContent = true;
                    break;
            }
        }

        // 文件末尾没有换行的最后一行
        if (rowHasContent)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }
}
