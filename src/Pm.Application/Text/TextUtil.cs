using System.Globalization;
using System.Text;

namespace Pm.Application.Text;

/// <summary>Текстовые примитивы: нормализация, ключевые слова, косинус, Жаккар.</summary>
public static class TextUtil
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "и","в","во","не","что","он","на","я","с","со","как","а","то","все","она","так","его","но","да",
        "ты","к","у","же","вы","за","бы","по","только","ее","мне","было","вот","от","меня","еще","нет",
        "о","из","ему","теперь","когда","даже","ну","вдруг","ли","если","уже","или","ни","быть","был",
        "него","до","вас","нибудь","опять","уж","вам","ведь","там","потом","себя","ничего","ей","они",
        "тут","где","есть","надо","ней","для","мы","тебя","их","чем","была","сам","чтоб","без","будто",
        "чего","раз","тоже","себе","под","будет","ж","тогда","кто","этот","того","потому","этого","какой",
        "совсем","ним","здесь","этом","один","почти","мой","тем","чтобы","нее","сейчас","были","куда",
        "зачем","всех","никогда","можно","при","наконец","два","об","другой","хоть","после","над","больше",
        "тот","через","эти","нас","про","всего","них","какая","много","разве","три","эту","моя","впрочем",
        "хорошо","свою","этой","перед","иногда","лучше","чуть","том","нельзя","такой","им","более","всегда",
        "конечно","всю","между","пожалуйста","спасибо","коллеги","ок","да","нужно","нужен","нужна"
    };

    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLower(CultureInfo.GetCultureInfo("ru-RU")))
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else sb.Append(' ');
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Грубая нормализация словоформы: обрезаем частые русские окончания.
    /// Стеммера в зависимостях нет, а для сопоставления коротких заголовков этого достаточно.
    /// </summary>
    public static string Stem(string word)
    {
        if (word.Length <= 4) return word;
        string[] endings =
        [
            "ированием","ированию","ированные","ирования","ование","ования","ованию",
            "ами","ями","ов","ев","ый","ий","ая","яя","ое","ее","ые","ие","ом","ем","ах","ях",
            "ую","юю","ой","ей","ам","ям","у","ю","а","я","ы","и","о","е","ь"
        ];
        foreach (var e in endings)
        {
            if (word.Length - e.Length >= 4 && word.EndsWith(e, StringComparison.Ordinal))
                return word[..^e.Length];
        }

        return word;
    }

    public static HashSet<string> Keywords(string text)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (w.Length < 3 || StopWords.Contains(w)) continue;
            result.Add(Stem(w));
        }

        return result;
    }

    public static double Jaccard<T>(IEnumerable<T> a, IEnumerable<T> b)
    {
        var sa = a as ISet<T> ?? new HashSet<T>(a);
        var sb = b as ISet<T> ?? new HashSet<T>(b);
        if (sa.Count == 0 && sb.Count == 0) return 0;
        var inter = sa.Count(sb.Contains);
        var union = sa.Count + sb.Count - inter;
        return union == 0 ? 0 : (double)inter / union;
    }

    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    /// <summary>Сравнение цитаты с текстом сообщения: игнорируем регистр, пробелы и типографские кавычки.</summary>
    public static bool ContainsQuote(string haystack, string quote)
    {
        if (string.IsNullOrWhiteSpace(quote)) return false;
        return Canon(haystack).Contains(Canon(quote), StringComparison.Ordinal);
    }

    private static string Canon(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
        {
            if (char.IsWhiteSpace(ch)) { if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); }
            else if (ch is '«' or '»' or '"' or '\'' or '‘' or '’' or '“' or '”') { }
            else if (ch is 'ё') sb.Append('е');
            else sb.Append(ch);
        }

        return sb.ToString().Trim();
    }

    public static string Shorten(string s, int max)
        => s.Length <= max ? s : s[..Math.Max(0, max - 1)].TrimEnd() + "…";
}
