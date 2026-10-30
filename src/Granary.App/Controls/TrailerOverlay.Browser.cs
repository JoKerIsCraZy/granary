using System.Text;
using Granary.Core;
using Granary.Core.Trailers;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;
using Windows.System;

namespace Granary.App.Controls;

// The trailer's WebView2: made for each trailer and closed with it, which stops the video and lets its browser go. It is
// locked down: it loads only Granary's own page and YouTube's embed (TrailerWebPolicy), opens no window of its own, grants
// nothing but autoplay, and hears only Granary's page (docs/architecture.md, "Trailers").
public sealed partial class TrailerOverlay
{
    // The player may play at once: the click that opened it was in Granary, not in the page.
    private const string BrowserArguments = "--autoplay-policy=no-user-gesture-required";

    // One environment for the process, with its user data folder under Granary's data directory (GRANARY_DATA_DIR too), run
    // by Windows' own Evergreen WebView2 runtime. Made again after a failure: a runtime installed meanwhile is found.
    private static Task<CoreWebView2Environment>? _environment;

    private WebView2? _web;
    private CoreWebView2? _core;
    private CoreWebView2Environment? _coreEnvironment;

    private async void StartBrowser(WebView2 web)
    {
        try
        {
            CoreWebView2Environment environment;
            try
            {
                environment = await EnvironmentAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("WebView2 is not available: {Error} 0x{Code:X8}", ex.GetType().Name, ex.HResult);
                Fail(web, TrailerFailure.NoRuntime);
                return;
            }
            if (!ReferenceEquals(_web, web))
            {
                return;
            }
            // InPrivate: YouTube's cookies and storage last only as long as the trailer, never on disk.
            var controller = environment.CreateCoreWebView2ControllerOptions();
            controller.IsInPrivateModeEnabled = true;
            await web.EnsureCoreWebView2Async(environment, controller);
            // Closed while it started: TearDown has let it go.
            if (!ReferenceEquals(_web, web) || web.CoreWebView2 is not { } core)
            {
                return;
            }
            _coreEnvironment = environment;
            _core = core;
            LockDown(core);
            core.Navigate(TrailerPage.Url.AbsoluteUri);
        }
        catch (Exception ex) when (!ReferenceEquals(_web, web))
        {
            // Closed while it started: its start broke off with it.
            _logger.LogDebug("A closed trailer's browser stopped starting: {Error}", ex.GetType().Name);
        }
        catch (Exception ex)
        {
            // async void: nothing may escape.
            _logger.LogWarning("The trailer's browser did not start: {Error} 0x{Code:X8}", ex.GetType().Name, ex.HResult);
            Fail(web, TrailerFailure.BrowserFailed);
        }
    }

    private static Task<CoreWebView2Environment> EnvironmentAsync()
    {
        if (_environment is { IsFaulted: false, IsCanceled: false } made)
        {
            return made;
        }
        var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = BrowserArguments };
        return _environment = CoreWebView2Environment.CreateWithOptionsAsync("", AppPaths.WebView2Directory, options).AsTask();
    }

    private void Fail(WebView2 web, TrailerFailure failure)
    {
        if (ReferenceEquals(_web, web))
        {
            ViewModel.Fail(failure);
        }
    }

    // Nothing the page does reaches past it: no developer tools, menus, status bar, zoom, browser keys, autofill, saved
    // passwords or host objects; only web messages, which carry the player's state.
    private void LockDown(CoreWebView2 core)
    {
        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsScriptEnabled = true;
        settings.IsWebMessageEnabled = true;
        core.AddWebResourceRequestedFilter(TrailerPage.Filter, CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnPageRequested;
        core.NavigationStarting += OnNavigationStarting;
        core.FrameNavigationStarting += OnFrameNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.WebMessageReceived += OnWebMessageReceived;
        core.ProcessFailed += OnProcessFailed;
        core.ContainsFullScreenElementChanged += OnFullScreenChanged;
        core.PermissionRequested += OnPermissionRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
        core.BasicAuthenticationRequested += OnBasicAuthenticationRequested;
    }

    private void TearDown()
    {
        if (_core is { } core)
        {
            _core = null;
            core.WebResourceRequested -= OnPageRequested;
            core.NavigationStarting -= OnNavigationStarting;
            core.FrameNavigationStarting -= OnFrameNavigationStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.WebMessageReceived -= OnWebMessageReceived;
            core.ProcessFailed -= OnProcessFailed;
            core.ContainsFullScreenElementChanged -= OnFullScreenChanged;
            core.PermissionRequested -= OnPermissionRequested;
            core.DownloadStarting -= OnDownloadStarting;
            core.LaunchingExternalUriScheme -= OnLaunchingExternalUriScheme;
            core.BasicAuthenticationRequested -= OnBasicAuthenticationRequested;
        }
        _coreEnvironment = null;
        if (_web is { } web)
        {
            _web = null;
            WebHost.Child = null;
            web.Close();
        }
    }

    // Granary answers its own host: the page for the trailer shown, and nothing else.
    private void OnPageRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (_coreEnvironment is not { } environment)
        {
            return;
        }
        args.Response = args.Request.Method == "GET" && TrailerWebPolicy.AllowsNavigation(args.Request.Uri) && ViewModel.Trailer is { } trailer
            ? environment.CreateWebResourceResponse(PageContent(TrailerPage.Html(trailer.Key)), 200, "OK", TrailerPage.Headers)
            : environment.CreateWebResourceResponse(null, 404, "Not Found", "");
    }

    private static IRandomAccessStream PageContent(string html) => new MemoryStream(Encoding.UTF8.GetBytes(html)).AsRandomAccessStream();

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!TrailerWebPolicy.AllowsNavigation(args.Uri))
        {
            args.Cancel = true;
            LogBlocked("A navigation", args.Uri);
        }
    }

    private void OnFrameNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!TrailerWebPolicy.AllowsFrame(args.Uri))
        {
            args.Cancel = true;
            LogBlocked("A frame", args.Uri);
        }
    }

    // The player's YouTube logo and title open a window: YouTube's pages go to the browser, nothing opens in Granary.
    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (TrailerWebPolicy.BrowserTarget(args.Uri) is { } target)
        {
            OpenInBrowser(target);
        }
        else
        {
            LogBlocked("A new window", args.Uri);
        }
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (TrailerWebPolicy.IsOwnOrigin(args.Source) && TrailerPageMessage.Parse(args.WebMessageAsJson) is { } message)
        {
            ViewModel.Receive(message);
        }
    }

    private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        _logger.LogWarning("A WebView2 process of the trailer failed: {Kind}", args.ProcessFailedKind);
        ViewModel.Fail(TrailerFailure.BrowserFailed);
    }

    // The player may play by itself; nothing else is granted.
    private void OnPermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args) =>
        args.State = args.PermissionKind == CoreWebView2PermissionKind.Autoplay ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;

    private void OnDownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args) => args.Cancel = true;

    private void OnLaunchingExternalUriScheme(CoreWebView2 sender, CoreWebView2LaunchingExternalUriSchemeEventArgs args) => args.Cancel = true;

    private void OnBasicAuthenticationRequested(CoreWebView2 sender, CoreWebView2BasicAuthenticationRequestedEventArgs args) => args.Cancel = true;

    // The player asked for full screen (its button, or a double-click): the window goes full screen with it, as Granary's
    // own player does, and back when it leaves.
    private void OnFullScreenChanged(CoreWebView2 sender, object args) => SetFullScreen(sender.ContainsFullScreenElement);

    private async void LeavePageFullScreen()
    {
        try
        {
            if (_core is { } core)
            {
                await core.ExecuteScriptAsync("if (document.fullscreenElement) { document.exitFullscreen(); }");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("The trailer's page did not leave full screen: {Error}", ex.GetType().Name);
        }
    }

    // The address stays out of the log: a YouTube address carries the video's key. Its host alone, and only for debugging.
    private void LogBlocked(string what, string? url)
    {
        _logger.LogInformation("{What} of the trailer's page was blocked", what);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            string host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.IdnHost}" : "(not a URL)";
            _logger.LogDebug("{What} was blocked: {Host}", what, host);
        }
    }

    private async void OpenInBrowser(Uri target)
    {
        try
        {
            if (!await Launcher.LaunchUriAsync(target))
            {
                _logger.LogWarning("The browser did not open the YouTube link");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("The YouTube link could not be opened: {Error}", ex.GetType().Name);
        }
    }
}
