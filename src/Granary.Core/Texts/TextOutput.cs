using System.Text;

namespace Granary.Core.Texts;

/// <summary>
/// What formatting one message writes: its text, its runs when asked for (each with the tags around it), and what was
/// wrong with the values it was given.
/// </summary>
internal sealed class TextOutput(bool keepRuns)
{
    private readonly StringBuilder _text = new();
    private readonly List<TextRun>? _runs = keepRuns ? [] : null;
    private readonly List<string> _tags = [];
    private List<string>? _problems;
    // Nothing was written since the last tag opened: closing it leaves an empty run that marks its place.
    private bool _emptyTag;

    public IReadOnlyList<string> Problems => _problems ?? [];

    public void Append(string text)
    {
        _text.Append(text);
        _emptyTag &= text.Length == 0;
    }

    public void Open(string tag)
    {
        EndRun();
        _tags.Add(tag);
        _emptyTag = true;
    }

    public void Close()
    {
        EndRun();
        // An empty tag ("<match></match>") is where the web puts a control: its place is kept as a run without text.
        if (_emptyTag && _runs is not null)
        {
            _runs.Add(new TextRun("", [.. _tags]));
        }
        _emptyTag = false;
        _tags.RemoveAt(_tags.Count - 1);
    }

    public void Problem(string problem) => (_problems ??= []).Add(problem);

    public string ToText() => _runs is null ? _text.ToString() : string.Concat(ToRuns().Select(run => run.Text));

    public IReadOnlyList<TextRun> ToRuns()
    {
        EndRun();
        return _runs ?? [new TextRun(_text.ToString(), [])];
    }

    // The text so far becomes a run with the tags around it, or joins the run before when that has the same tags.
    private void EndRun()
    {
        if (_runs is null || _text.Length == 0)
        {
            return;
        }
        string text = _text.ToString();
        _text.Clear();
        if (_runs.Count > 0 && _runs[^1].Tags.SequenceEqual(_tags))
        {
            _runs[^1] = _runs[^1] with { Text = _runs[^1].Text + text };
        }
        else
        {
            _runs.Add(new TextRun(text, [.. _tags]));
        }
    }
}
