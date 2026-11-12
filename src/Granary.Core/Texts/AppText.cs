namespace Granary.Core.Texts;

/// <summary>
/// Granary's texts in the language on screen, for XAML and view models alike, by the keys of Silo's catalogs: the web's
/// (<c>common.cancel</c>, <c>web.…</c>) and Granary's own (<c>granary.…</c>). It is what the web's <c>t(id, values)</c>
/// is (web/src/i18n/README.md, "The API"). In XAML, a static function with a constant key:
/// <c>Text="{x:Bind t:AppText.Get('common.cancel')}"</c> with <c>xmlns:t="using:Granary.Core.Texts"</c>; with values,
/// <c>{x:Bind t:AppText.Get('web.home.title', 'count', ViewModel.Count)}</c>. In C#,
/// <c>AppText.Get("web.collections.copy.inCollections", ("count", n), ("count_text", text))</c>.
/// <para>
/// The process has one language, which the app applies at start-up, before its first window. Until then, and in tests,
/// it is English. A test that wants another language uses one for its own async flow (<see cref="Use"/>), so the tests
/// running beside it keep English.
/// </para>
/// </summary>
public static class AppText
{
    private static readonly Lazy<TextLanguage> English = new(() => TextLanguage.Load(AppLanguage.English));
    private static readonly AsyncLocal<TextLanguage?> InThisFlow = new();
    private static TextLanguage? _applied;

    /// <summary>The language texts come in now.</summary>
    public static TextLanguage Language => InThisFlow.Value ?? Volatile.Read(ref _applied) ?? English.Value;

    /// <summary>Makes a language the process's, for every text from now on. The app does this at start-up.</summary>
    public static void Apply(TextLanguage language) => Volatile.Write(ref _applied, language);

    /// <summary>
    /// Uses a language in the calling async flow only, until disposed: for tests, which run in parallel and must not
    /// change the language of the others.
    /// </summary>
    public static IDisposable Use(TextLanguage language)
    {
        var before = InThisFlow.Value;
        InThisFlow.Value = language;
        return new Restore(before);
    }

    /// <summary>A text without values.</summary>
    public static string Get(string key) => Language.Get(key);

    /// <summary>A text with one value, for XAML: x:Bind tells overloads apart only by their number of arguments.</summary>
    public static string Get(string key, string name, object? value) => Language.Get(key, [(name, value)]);

    /// <summary>A text with two values, for XAML.</summary>
    public static string Get(string key, string name1, object? value1, string name2, object? value2) =>
        Language.Get(key, [(name1, value1), (name2, value2)]);

    /// <summary>A text with any number of values: <c>AppText.Get(key, ("count", n), ("name", name))</c>.</summary>
    public static string Get(string key, params ReadOnlySpan<(string Name, object? Value)> values) => Language.Get(key, values);

    /// <summary>A text as runs, each with the rich-text tags around it, for a page to build inlines from.</summary>
    public static IReadOnlyList<TextRun> Runs(string key, params ReadOnlySpan<(string Name, object? Value)> values) =>
        Language.Runs(key, values);

    /// <summary>
    /// A text in capitals by the rules of the language on screen, where the web writes it in mixed case and its CSS shows
    /// it upper-cased (<c>uppercase</c>), as the sidebar's section names. Scripts without capitals stay as they are.
    /// </summary>
    public static string Upper(string text) => text.ToUpper(Language.Culture);

    /// <summary>A number as the language on screen writes it (<see cref="TextLanguage.Number"/>).</summary>
    public static string Number(decimal number) => Language.Number(number);

    /// <summary>
    /// Text a Silo server sent in English with its catalog key, such as a row's title: translated where the language on
    /// screen has the key, else as sent (<see cref="TextLanguage.ServerText"/>; the web's <c>serverText</c>).
    /// </summary>
    public static string ServerText(string? key, IReadOnlyDictionary<string, string>? values, string serverText) =>
        Language.ServerText(key, values, serverText);

    private sealed class Restore(TextLanguage? before) : IDisposable
    {
        public void Dispose() => InThisFlow.Value = before;
    }
}
