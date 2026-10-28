using Granary.App.Services;
using Granary.App.Views;
using Granary.Core.Abstractions;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Diagnostics;
using Microsoft.UI.Xaml.Media;

namespace Granary.App;

// The player over the window (IPlayerOverlay): the whole window while it plays, title bar included, as the player page
// always had it, with the app hidden under it; minimized, the bar at the bottom of the page beside the sidebar, which
// goes on down to the window's edge, the page the title was played from still there and ending above the bar. A new
// title replaces the one that plays.
public sealed partial class MainWindow : IPlayerOverlay
{
    // The bar: its height and its distance from the window's edges.
    private const double DockHeight = 100;
    private const double DockInset = 8;
    private const double DockNoticeGap = 12;

    // The settings and the admin area bring their own sidebar, as wide as the app's (SettingsPage, AdminPage).
    private const double SettingsSidebarWidth = 260;

    // As the rail opens over the page: the bar's edge moves aside with it, easing out.
    private static readonly TimeSpan DockSlide = TimeSpan.FromMilliseconds(250);

    private PlayerPage? _player;
    private double _dockLeft;
    private double _dockSlideFrom;
    private double _dockSlideTo;
    private long _dockSlideStart;
    private bool _isDockSliding;

    public bool IsPlayerOpen => _player is not null;

    public bool IsPlayerFull => _player is { IsDocked: false };

    public void Play(object? request)
    {
        if (request is not PlayRequest play)
        {
            return;
        }
        if (_player is { } playing)
        {
            ClosePlayer();
            // The engine's picture goes to one panel at a time: the new player takes it once the old one let it go.
            if (playing.IsLoaded)
            {
                playing.Unloaded += OpenAfterUnloaded;
                return;

                void OpenAfterUnloaded(object sender, RoutedEventArgs e)
                {
                    playing.Unloaded -= OpenAfterUnloaded;
                    Open(play);
                }
            }
        }
        Open(play);
    }

    private void Open(PlayRequest play)
    {
        var page = new PlayerPage();
        page.MinimizeRequested += (_, _) => Dock();
        page.ExpandRequested += (_, _) => Expand();
        page.StopRequested += (_, _) => ClosePlayer();
        page.ViewModel.PropertyChanged += OnPlayerChanged;
        _player = page;
        PlayerHost.Child = page;
        PlayerHost.Visibility = Visibility.Visible;
        Expand();
        page.Start(play);
    }

    public void ClosePlayer()
    {
        if (_player is not { } page)
        {
            return;
        }
        _player = null;
        page.ViewModel.PropertyChanged -= OnPlayerChanged;
        StopDockSlide();
        page.Leave();
        // Unloaded, the page lets go of its view model and the engine's picture; one never loaded lets go of it here.
        if (!page.IsLoaded)
        {
            page.ViewModel.Dispose();
        }
        PlayerHost.Child = null;
        PlayerHost.Visibility = Visibility.Collapsed;
        ShowApp(true);
        ShowDockSpace();
        ShowDockNotice();
    }

    private void Dock()
    {
        if (_player is not { } page)
        {
            return;
        }
        page.IsDocked = true;
        Grid.SetRow(PlayerHost, 1);
        Grid.SetRowSpan(PlayerHost, 1);
        PlayerHost.VerticalAlignment = VerticalAlignment.Bottom;
        PlayerHost.Height = DockHeight;
        ShowDockSpace();
        ShowApp(true);
        PlaceDock(animate: false);
        ShowDockNotice();
        // Played from the start (--play), nothing is under the player yet.
        if (ContentFrame.Content is null)
        {
            _navigation.ShowSection(AppRoute.Home);
        }
        // The keyboard goes back to the page: the player's keys were its own.
        if (FocusManager.FindFirstFocusableElement(ContentFrame) is UIElement first)
        {
            first.Focus(FocusState.Programmatic);
        }
    }

    private void Expand()
    {
        if (_player is not { } page)
        {
            return;
        }
        Grid.SetRow(PlayerHost, 0);
        Grid.SetRowSpan(PlayerHost, 2);
        StopDockSlide();
        PlayerHost.VerticalAlignment = VerticalAlignment.Stretch;
        PlayerHost.Height = double.NaN;
        PlayerHost.Margin = default;
        ShowApp(false);
        page.IsDocked = false;
        ShowDockSpace();
        ShowDockNotice();
    }

    // A notice the server or an administrator sent while the player is the bar shows above the bar, so it is seen there
    // too; the full player shows its own.
    private void OnPlayerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.Notice))
        {
            ShowDockNotice();
        }
    }

    private void ShowDockNotice()
    {
        if (_player is not { IsDocked: true, ViewModel: { Notice: { } notice } })
        {
            DockNotice.Visibility = Visibility.Collapsed;
            return;
        }
        DockNotice.NoticeTitle = notice.Title;
        DockNotice.Message = notice.Message;
        DockNotice.IsWarning = notice.IsWarning;
        DockNotice.Visibility = Visibility.Visible;
        PlaceDockNotice();
    }

    // Its left edge is the bar's, as the bar slides; its foot a little above the bar's top.
    private void PlaceDockNotice() =>
        DockNotice.Margin = new Thickness(_dockLeft, 0, DockInset, DockHeight + DockInset + DockNoticeGap);

    // The page ends above the bar, so nothing hides under it. Settings and Admin bring a sidebar of their own, which runs
    // to the window's foot beside the bar as the app's does: they keep their whole height and end their own page above it.
    private void ShowDockSpace()
    {
        double reserved = _player is { IsDocked: true } ? DockHeight + (2 * DockInset) : 0;
        if (ContentFrame.Content is IDockSpace page)
        {
            ContentFrame.Margin = default;
            page.ReserveBottom(reserved);
        }
        else
        {
            ContentFrame.Margin = new Thickness(0, 0, 0, reserved);
        }
    }

    // The bar starts right of the sidebar, as wide as the sidebar is: open, the rail of a title's page, or the settings'
    // own. Read from the sidebar's state rather than the page's layout, which lags behind a change of the sidebar's mode
    // (Back from a title's page to Home left the bar on the rail's edge, over the open sidebar). The rail opens over the
    // page: the bar moves aside with it, so the pane (Settings and the profile at its foot) is reached and the picture
    // still plays beside it, and comes back as the rail closes; a sidebar that opens or narrows takes the bar along too.
    private void PlaceDock(bool animate) => PlaceDock(animate, Navigation.IsPaneOpen);

    private void PlaceDock(bool animate, bool paneOpen)
    {
        if (_player is not { IsDocked: true })
        {
            return;
        }
        double left = !Navigation.IsPaneVisible ? (_navigation.Current is AppRoute.Settings or AppRoute.Admin ? SettingsSidebarWidth : 0)
            : paneOpen && Navigation.PaneDisplayMode is NavigationViewPaneDisplayMode.Left or NavigationViewPaneDisplayMode.LeftCompact
                ? Navigation.OpenPaneLength
            : Navigation.PaneDisplayMode is NavigationViewPaneDisplayMode.LeftCompact or NavigationViewPaneDisplayMode.Left
                ? Navigation.CompactPaneLength
            : 0;
        double target = left + DockInset;
        if (!animate || Math.Abs(target - _dockLeft) < 0.5)
        {
            StopDockSlide();
            SetDockLeft(target);
            return;
        }
        if (_isDockSliding && Math.Abs(_dockSlideTo - target) < 0.5)
        {
            // Asked again on the way there: the slide goes on.
            return;
        }
        _dockSlideFrom = _dockLeft;
        _dockSlideTo = target;
        _dockSlideStart = Stopwatch.GetTimestamp();
        if (!_isDockSliding)
        {
            _isDockSliding = true;
            CompositionTarget.Rendering += OnDockSlideFrame;
        }
    }

    private void OnDockSlideFrame(object? sender, object e)
    {
        double t = Math.Clamp(Stopwatch.GetElapsedTime(_dockSlideStart) / DockSlide, 0, 1);
        double eased = 1 - Math.Pow(1 - t, 3);
        SetDockLeft(_dockSlideFrom + ((_dockSlideTo - _dockSlideFrom) * eased));
        if (t >= 1)
        {
            StopDockSlide();
        }
    }

    private void StopDockSlide()
    {
        if (_isDockSliding)
        {
            _isDockSliding = false;
            CompositionTarget.Rendering -= OnDockSlideFrame;
        }
    }

    private void SetDockLeft(double left)
    {
        _dockLeft = left;
        PlayerHost.Margin = new Thickness(left, 0, DockInset, DockInset);
        PlaceDockNotice();
    }

    // Under the full player the app is neither drawn nor reached, by the pointer, Tab or a screen reader; collapsed, it
    // keeps its pages, scroll positions and all.
    private void ShowApp(bool shown)
    {
        Navigation.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        PageBackButton.Opacity = shown ? 1 : 0;
        PageBackButton.IsHitTestVisible = shown;
    }
}
