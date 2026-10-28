using Granary.Core.Abstractions;

namespace Granary.Tests.Fakes;

/// <summary>Answers every question with <see cref="Answer"/> and keeps what was asked.</summary>
internal sealed class FakeConfirmation : IConfirmation
{
    public bool Answer { get; set; } = true;

    public List<(string Title, string Message, string Action)> Asked { get; } = [];

    public Task<bool> AskAsync(string title, string message, string action)
    {
        Asked.Add((title, message, action));
        return Task.FromResult(Answer);
    }
}
