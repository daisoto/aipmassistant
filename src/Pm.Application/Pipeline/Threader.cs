using Pm.Domain;

namespace Pm.Application.Pipeline;

public sealed class ThreaderOptions
{
    /// <summary>Размер блока сообщений, уходящего в модель за один вызов.</summary>
    public int WindowSize { get; set; } = 24;

    /// <summary>Перекрытие между соседними блоками.</summary>
    public int Overlap { get; set; } = 4;
}

/// <summary>
/// Разбивает источник на тематические блоки. В модель никогда не уходит одиночное сообщение:
/// реплика «Подходит» осмысленна только вместе с «Предлагаю ориентир 14 сентября».
/// </summary>
public sealed class Threader(ThreaderOptions? options = null)
{
    private readonly ThreaderOptions _opt = options ?? new ThreaderOptions();

    public IReadOnlyList<IReadOnlyList<Message>> Split(IReadOnlyList<Message> messages)
    {
        if (messages.Count == 0) return [];
        if (messages.Count <= _opt.WindowSize) return [messages];

        var blocks = new List<IReadOnlyList<Message>>();
        var step = Math.Max(1, _opt.WindowSize - _opt.Overlap);
        for (var start = 0; start < messages.Count; start += step)
        {
            var take = Math.Min(_opt.WindowSize, messages.Count - start);
            blocks.Add(messages.Skip(start).Take(take).ToList());
            if (start + take >= messages.Count) break;
        }

        return blocks;
    }
}
