using Granary.Core.Abstractions;

namespace Granary.Tests.Fakes;

/// <summary>
/// Answers every question with <see cref="Answer"/> and keeps what was asked; <see cref="WhileAsking"/> runs while the
/// question is open, for what changes before the viewer answers.
/// </summary>
internal sealed class FakeConfirmation : IConfirmation
{
    public bool Answer { get; set; } = true;

    public Func<Task>? WhileAsking { get; set; }

    public List<(string Title, string Message, string Action)> Asked { get; } = [];

    public async Task<bool> AskAsync(string title, string message, string action)
    {
        Asked.Add((title, message, action));
        if (WhileAsking is { } whileAsking)
        {
            await whileAsking();
        }
        return Answer;
    }
}
