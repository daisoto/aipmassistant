using System.Diagnostics;
using System.Text.RegularExpressions;
using Pm.Application;
using Pm.Application.Text;
using Pm.Domain;

namespace Pm.Infrastructure.Llm;

/// <summary>
/// Baseline на правилах, реализующий тот же интерфейс, что и языковая модель.
///
/// Нужен для трёх вещей: приложение работает сразу после clone без поднятой модели;
/// прогонщик метрик даёт нижнюю границу качества, с которой сравнивается LLM;
/// поведение детерминировано, поэтому на нём проверяются резолвер, парсер сроков и post-meeting.
///
/// Заменять его LLM-провайдером — не «переписать», а поменять Llm:Provider в конфиге.
/// </summary>
public sealed partial class HeuristicLlmClient(IPmStore store, LlmRunContext run) : ILlmClient
{
    public string Name => "heuristic-baseline";

    /// <summary>
    /// Baseline пишет журнал наравне с моделью. Иначе на конфигурации по умолчанию
    /// таблица пуста, и сравнить два провайдера по журналу нельзя.
    /// </summary>
    private async Task LogAsync(string operation, int promptChars, int responseChars, long elapsedMs)
    {
        try
        {
            await store.AddLlmCallAsync(new LlmCall
            {
                Id = Guid.NewGuid().ToString("n"),
                CorrelationId = run.CorrelationId,
                Operation = operation,
                Provider = Name,
                ProjectId = run.ProjectId,
                SourceId = run.SourceId,
                SchemaMode = "none",
                PromptChars = promptChars,
                ResponseChars = responseChars,
                ElapsedMs = elapsedMs
            }, CancellationToken.None);
        }
        catch
        {
            // ponytail: журнал baseline не стоит того, чтобы ронять прогон. Провайдер
            // с настоящей моделью логирует отказ записи через ILogger.
        }
    }

    /// <summary>Откалиброван по стресс-тесту: при 0.72 смешанные сообщения давали дубли.</summary>
    public const double MergeThreshold = 0.58;

    private static readonly string[] AckOnly =
        ["ок", "окей", "хорошо", "спасибо", "принято", "приняли", "понял", "поняла", "ясно", "супер", "отлично"];

    private static readonly string[] Confirmations =
        ["да", "подходит", "верно", "все верно", "всё верно", "согласен", "согласны", "именно", "так точно"];

    private static readonly string[] RequirementMarkers =
        ["хотим", "хотелось", "нужен", "нужна", "нужно", "нужны", "добавить", "просим", "должен", "должна",
         "требуется", "необходимо", "оставить", "убрать", "показывать", "сделать"];

    private static readonly string[] PromiseMarkers =
        ["вернёмся", "вернемся", "ответим", "сообщим", "проверим", "уточним", "подготовим", "посчитаем", "дадим ответ"];

    private static readonly string[] TeamTaskMarkers =
        ["посмотрите", "проверьте", "сделайте", "нужна проверка", "нужно проверить", "прошу проверить"];

    private static readonly string[] ClientPromiseMarkers =
        ["пришлю", "передам", "запрошу", "предоставим", "подготовим доступ", "дам", "дадим", "вышлю", "отправлю"];

    private static readonly string[] OpenQuestionMarkers =
        ["не решили", "пока не", "не определен", "не определён", "открытым вопросом", "открытый вопрос",
         "выбираем между", "не выбрали", "не приняли решение"];

    private static readonly string[] ContractorMarkers =
        ["подрядчик", "интегратор", "databridge"];

    private static readonly string[] SupersedeMarkers =
        ["стоп", "вместо", "поменялось", "передумали", "отменяем", "не убираем", "оставить"];

    private static readonly string[] DeadlinePatterns =
    [
        @"до\s+конца\s+недели", @"до\s+конца\s+дня", @"завтра\s+до\s+конца\s+дня",
        @"завтра\s+до\s+\d{1,2}[:.]\d{2}", @"сегодня\s+до\s+\d{1,2}[:.]\d{2}",
        @"до\s+\d{1,2}[:.]\d{2}\s+\d{1,2}\s+[а-яё]{3,}", @"до\s+\d{1,2}\s+[а-яё]{3,}",
        @"\d{1,2}\s+[а-яё]{3,}(?:\s+\d{4})?", @"в течение\s+\d+\s+рабочих\s+дн[а-яё]+",
        @"до\s+завтра\s+\d{1,2}[:.]\d{2}", @"до\s+\d{1,2}[:.]\d{2}",
        @"к\s+(?:понедельник|вторник|сред|четверг|пятниц|суббот|воскресень)[а-яё]*",
        @"до\s+(?:понедельник|вторник|сред|четверг|пятниц|суббот|воскресень)[а-яё]*",
        @"срок не критичн[а-яё]*", @"в следующем спринте", @"в следующем плановом цикле", @"позже",
        @"завтра", @"сегодня"
    ];

    public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new ExtractionResult();
        var block = request.Block;
        Candidate? previous = null;

        for (var i = 0; i < block.Count; i++)
        {
            var message = block[i];
            var text = message.Text.Trim();
            var lower = text.ToLowerInvariant().Replace('ё', 'е');

            // Подтверждения не шум: они закрепляют срок или решение предыдущего кандидата.
            if (IsShortAck(lower, Confirmations) && previous is not null)
            {
                previous.EvidenceMessageIds.Add(message.Id);
                continue;
            }

            if (IsShortAck(lower, AckOnly) || text.Length < 6)
            {
                result.NoiseMessageIds.Add(message.Id);
                continue;
            }

            // Вопрос менеджера сам по себе задачей не является — он лишь уточняет требование.
            if (message.AuthorSide == Side.Xpage && text.EndsWith('?') && previous is not null)
            {
                previous.EvidenceMessageIds.Add(message.Id);
                continue;
            }

            var candidate = Build(message, lower, text, i, block);
            if (candidate is null)
            {
                result.NoiseMessageIds.Add(message.Id);
                continue;
            }

            candidate.TempId = $"c{result.Candidates.Count + 1}";
            result.Candidates.Add(candidate);
            previous = candidate;
        }

        await LogAsync("extraction", block.Sum(m => m.Text.Length), result.Candidates.Count, sw.ElapsedMilliseconds);
        return result;
    }

    private static Candidate? Build(
        Message message, string lower, string text, int index, IReadOnlyList<Message> block)
    {
        var kind = ClassifyKind(lower, message.AuthorSide);
        if (kind is null) return null;

        var side = ClassifySide(lower, message.AuthorSide, kind.Value);
        var promise = message.AuthorSide == Side.Xpage
                      && PromiseMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal))
                      && !TeamTaskMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal));

        var candidate = new Candidate
        {
            Kind = kind.Value,
            Side = side,
            Title = MakeTitle(text),
            Body = text,
            IsPromiseToClient = promise,
            EvidenceMessageIds = [message.Id],
            Rationale = $"baseline: {kind}/{side}"
        };

        var quote = FindDeadline(text);
        if (quote is not null)
            candidate.Deadline = new Attributed<string>(quote, quote, message.Id);

        var assignee = FindAssignee(lower, index, block);
        if (assignee is not null)
            candidate.Assignee = new Attributed<string>(assignee, assignee, message.Id);

        return candidate;
    }

    private static ItemKind? ClassifyKind(string lower, Side author)
    {
        if (OpenQuestionMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal)))
            return ItemKind.OpenQuestion;

        if (ContractorMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal)))
            return ItemKind.Dependency;

        if (author == Side.Xpage)
        {
            if (PromiseMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal))
                || TeamTaskMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal)))
                return ItemKind.Task;

            if (lower.Contains("фиксирую", StringComparison.Ordinal)
                || lower.Contains("зафиксировали", StringComparison.Ordinal))
                return ItemKind.Decision;

            return null;
        }

        if (ClientPromiseMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal)))
            return ItemKind.Task;

        if (RequirementMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal)))
            return ItemKind.Requirement;

        return null;
    }

    private static Side ClassifySide(string lower, Side author, ItemKind kind)
    {
        if (ContractorMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal)))
            return Side.Contractor;

        return kind switch
        {
            // Требование заказчика реализует команда.
            ItemKind.Requirement => Side.Xpage,
            ItemKind.Task => author == Side.Client ? Side.Client : Side.Xpage,
            ItemKind.OpenQuestion => author == Side.Client ? Side.Client : Side.Xpage,
            _ => author
        };
    }

    private static string? FindAssignee(string lower, int index, IReadOnlyList<Message> block)
    {
        if (!TeamTaskMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal))) return null;

        // Исполнитель считается названным, если следующая реплика принадлежит роли команды.
        for (var i = index + 1; i < Math.Min(index + 3, block.Count); i++)
        {
            var name = block[i].AuthorName;
            if (name.Contains("Разработчик", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Аналитик", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Дизайнер", StringComparison.OrdinalIgnoreCase))
                return name;
        }

        return null;
    }

    private static string? FindDeadline(string text)
    {
        var lower = text.ToLowerInvariant().Replace('ё', 'е');
        foreach (var pattern in DeadlinePatterns)
        {
            var match = Regex.Match(lower, pattern, RegexOptions.IgnoreCase);
            if (!match.Success) continue;

            // Возвращаем фрагмент исходного текста, чтобы валидатор цитат нашёл его дословно.
            return text.Substring(match.Index, match.Length);
        }

        return null;
    }

    private static string MakeTitle(string text)
    {
        var cleaned = TitleNoiseRegex().Replace(text, "").Trim();
        var sentenceSeparators = new[] { '.', '!', '?' };
        var sentences = cleaned.Split(sentenceSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        var sentence = sentences.FirstOrDefault(s => s.Length >= 15)
                       ?? sentences.FirstOrDefault()
                       ?? cleaned;
        if (sentence.Length == 0) sentence = text.Trim();
        return TextUtil.Shorten(char.ToUpperInvariant(sentence[0]) + sentence[1..], 120);
    }

    private static bool IsShortAck(string lower, string[] set)
    {
        var trimmed = lower.Trim(' ', '.', '!', ',');
        return set.Contains(trimmed, StringComparer.Ordinal)
               || (trimmed.Length <= 24 && set.Any(a => trimmed.StartsWith(a + " ", StringComparison.Ordinal)
                                                        || trimmed == a));
    }

    public async Task<ResolveDecision> ResolveAsync(ResolveRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var decision = Decide(request);
        await LogAsync("resolve", request.Candidate.Body.Length, decision.Reason.Length, sw.ElapsedMilliseconds);
        return decision;
    }

    private static ResolveDecision Decide(ResolveRequest request)
    {
        var best = request.Nearest.FirstOrDefault();
        if (best is null)
            return new ResolveDecision(ResolveOp.New, null, null, "baseline: похожих нет", 0.9);

        var lower = request.Candidate.Body.ToLowerInvariant().Replace('ё', 'е');
        var contradicts = SupersedeMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal));

        if (contradicts && best.Score >= 0.50 && best.Item.Kind is ItemKind.Requirement or ItemKind.Decision)
        {
            return new ResolveDecision(
                ResolveOp.Supersede, best.Item.Id, null,
                $"baseline: маркер отмены при близости {best.Score:F2}", 0.7);
        }

        if (best.Score >= MergeThreshold)
        {
            var patch = new WorkItemPatch
            {
                DeadlineQuote = request.Candidate.Deadline?.Quote,
                DeadlineMessageId = request.Candidate.Deadline?.SourceMessageId,
                Assignee = request.Candidate.Assignee?.Value
            };
            return new ResolveDecision(
                ResolveOp.Update, best.Item.Id, patch,
                $"baseline: близость {best.Score:F2} выше порога слияния", 0.7);
        }

        return new ResolveDecision(
            ResolveOp.New, null, null, $"baseline: близость {best.Score:F2} ниже порога слияния", 0.6);
    }

    /// <summary>Baseline формулировки не трогает — PhrasingGuard такой ответ принимает без замечаний.</summary>
    public Task<IReadOnlyList<PostMeetingSection>> PolishAsync(
        IReadOnlyList<PostMeetingSection> draft, CancellationToken ct = default)
        => Task.FromResult(draft);

    [GeneratedRegex(@"^(доброе утро|добрый день|здравствуйте|коллеги|привет|тема:\s*[^.]*\.)[!,.\s]*", RegexOptions.IgnoreCase)]
    private static partial Regex TitleNoiseRegex();
}
