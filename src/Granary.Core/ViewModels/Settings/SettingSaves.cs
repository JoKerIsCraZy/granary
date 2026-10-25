using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// A settings page's writes, one after another in the order the viewer made them, so the last choice is the one the
/// server keeps. Each reports whether it went through; nothing it meets escapes to the page.
/// </summary>
internal sealed class SettingSaves(ILogger logger)
{
    private Task _last = Task.CompletedTask;

    public Task RunAsync(Func<Task> write, Action<bool> done)
    {
        var previous = _last;
        _last = RunAfterAsync();
        return _last;

        async Task RunAfterAsync()
        {
            await previous;
            bool saved;
            try
            {
                await write();
                saved = true;
            }
            catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
            {
                logger.LogInformation("A setting could not be saved: {Failure}", ApiFailureText.Describe(ex));
                saved = false;
            }
            catch (Exception ex)
            {
                // Signed out meanwhile, or something unforeseen: the control goes back, the page goes on.
                logger.LogError(ex, "Saving a setting failed unexpectedly");
                saved = false;
            }
            done(saved);
        }
    }
}
