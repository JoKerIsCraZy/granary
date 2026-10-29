using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api;
using Granary.Core.Servers;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Settings;

// Settings → Devices: Sign out, Rename, Use profile settings and Forget, and the next page of sign-ins.
public sealed partial class DevicesViewModel
{
    private const string SignOutNote = "A video already playing may continue until its next request. Saved device settings are kept.";

    [RelayCommand]
    private async Task LoadMoreSessionsAsync()
    {
        if (_nextCursor is not { } cursor || await TargetAsync() is not { } server)
        {
            return;
        }
        int load = _load;
        try
        {
            var page = await _devices.ListSessionsAsync(server.BaseUri, cursor);
            if (load != _load)
            {
                return;
            }
            // The same cursor again would load the same page forever.
            string? next = NextCursor(page);
            _nextCursor = next == cursor ? null : next;
            CanLoadMoreSessions = _nextCursor is not null;
            AddOthers(page.Items);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning("More sign-ins could not be read: {Failure}", ApiFailureText.Describe(ex));
            Status = "Couldn't load more sessions. Reload the list.";
        }
    }

    /// <summary>
    /// Signs a sign-in out after asking. Another one loses access on its next request; Granary's own signs Granary out
    /// of the server, back to the sign-in screen, as the web's own does for its browser.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SignOutSessionAsync(SignedInSessionRow? row)
    {
        if (row is null || row.IsBusy || await TargetAsync() is null || !Listed(row))
        {
            return;
        }
        bool asked = row.IsCurrent
            ? await _confirmation.AskAsync("Sign out this device?",
                "You'll return to the sign-in screen. You can sign in again with the same password or sign-in provider.\n\n" + SignOutNote, "Sign out")
            : await _confirmation.AskAsync($"Sign out {row.Name}?",
                "This client will lose access on its next authenticated request. Its other sessions and password stay unchanged.\n\n" + SignOutNote,
                "Sign out");
        // The server or profile may have changed while the question was open.
        if (!asked || await TargetAsync() is not { } server || !Listed(row))
        {
            return;
        }
        row.IsBusy = true;
        Status = "";
        try
        {
            if (row.IsCurrent)
            {
                await _session.SignOutAsync(server.Id);
                return;
            }
            await _devices.SignOutSessionAsync(server.BaseUri, row.Id);
            OtherSessions.Remove(row);
            Status = "Session signed out";
        }
        catch (SiloApiException ex) when (ex.Status == 404)
        {
            // Already gone: signed out elsewhere, or expired.
            OtherSessions.Remove(row);
            Status = "Session signed out";
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning("A sign-in could not be signed out: {Failure}", ApiFailureText.Describe(ex));
            Status = "Couldn't sign out the session. Try again.";
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    /// <summary>Saves the name typed for a device; an empty one shows the name it reports again.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SaveRenameAsync(UserDeviceRow? row)
    {
        if (row is null || row.IsBusy || await TargetAsync() is not { } server || !Listed(row))
        {
            return;
        }
        // The server counts characters, not UTF-16 units, and takes at most 120.
        string typed = string.Concat(row.EditName.Trim().EnumerateRunes().Take(120));
        string? name = typed.Length > 0 ? typed : null;
        if (name == row.Device.CustomName)
        {
            row.EndRename();
            return;
        }
        row.IsBusy = true;
        Status = "";
        try
        {
            var saved = await _devices.RenameDeviceAsync(server.BaseUri, row.DeviceId, name);
            row.Show(row.Device with { CustomName = saved.CustomName });
            row.EndRename();
            Status = name is null ? "Device name reset" : "Device renamed";
        }
        catch (SiloApiException ex) when (ex.Status == 404)
        {
            await GoneAsync(server.BaseUri);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning("A device could not be renamed: {Failure}", ApiFailureText.Describe(ex));
            Status = "Couldn't rename";
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    /// <summary>Removes the settings changed on a device after asking, so it follows the profile's again.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task UseProfileSettingsAsync(UserDeviceRow? row)
    {
        if (row is null || row.IsBusy || !row.CanUseProfileSettings || await TargetAsync() is null || !Listed(row))
        {
            return;
        }
        int changed = row.Device.ChangedCount;
        bool asked = await _confirmation.AskAsync($"Use your profile settings on {row.Name}?",
            $"This removes the {DeviceText.Count(changed, "setting")} changed on this device. Settings that only apply to devices go back to the app default.",
            "Use profile settings");
        if (!asked || await TargetAsync() is not { } server || !Listed(row))
        {
            return;
        }
        row.IsBusy = true;
        Status = "";
        try
        {
            await _devices.ClearDeviceSettingsAsync(server.BaseUri, row.DeviceId);
            Status = "Removed the changes on this device";
            await LoadDevicesAsync(server.BaseUri, _load, CancellationToken.None);
        }
        catch (SiloApiException ex) when (ex.Status == 404)
        {
            await GoneAsync(server.BaseUri);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning("A device's settings could not be cleared: {Failure}", ApiFailureText.Describe(ex));
            Status = "Couldn't clear";
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    /// <summary>Forgets a device after asking: its settings go, and it leaves the list until it is used again. Not this one.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ForgetAsync(UserDeviceRow? row)
    {
        if (row is null || row.IsBusy || !row.CanForget || await TargetAsync() is null || !Listed(row))
        {
            return;
        }
        bool asked = await _confirmation.AskAsync($"Forget {row.Name}?",
            "Its settings are removed and it disappears from this list until it's used again.", "Forget");
        if (!asked || await TargetAsync() is not { } server || !Listed(row))
        {
            return;
        }
        row.IsBusy = true;
        Status = "";
        try
        {
            await _devices.ForgetDeviceAsync(server.BaseUri, row.DeviceId);
            Remove(row);
            Status = "Device forgotten";
        }
        catch (SiloApiException ex) when (ex.Status == 404)
        {
            // Already gone: forgotten elsewhere.
            Remove(row);
            Status = "Device forgotten";
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning("A device could not be forgotten: {Failure}", ApiFailureText.Describe(ex));
            Status = "Couldn't forget";
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    /// <summary>
    /// The server to act on: the one the lists were read from, while it and the profile are still in use. Otherwise null,
    /// after the lists were read again for the ones in use now.
    /// </summary>
    private async Task<ServerEntry?> TargetAsync()
    {
        if (ReadForNow() && _session.ActiveServer is { } server)
        {
            return server;
        }
        await ReloadForNowAsync();
        Status = "The server or profile changed. The lists were read again.";
        return null;
    }

    // A row from the lists as they are now, not one an earlier read left behind.
    private bool Listed(SignedInSessionRow row) => ReferenceEquals(row, CurrentSession) || OtherSessions.Contains(row);

    private bool Listed(UserDeviceRow row) => DeviceGroups.Any(g => g.Devices.Contains(row));

    // A device the server no longer knows: removed or forgotten elsewhere, so the list is read again.
    private async Task GoneAsync(Uri server)
    {
        await LoadDevicesAsync(server, _load, CancellationToken.None);
        Status = "That device is no longer listed.";
    }

    private void Remove(UserDeviceRow row)
    {
        if (DeviceGroups.FirstOrDefault(g => g.Devices.Contains(row)) is not { } group)
        {
            return;
        }
        group.Devices.Remove(row);
        if (group.Devices.Count == 0)
        {
            DeviceGroups.Remove(group);
        }
    }
}
