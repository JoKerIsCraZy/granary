using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Granary.App.Interop;
using Granary.Core.Remote;
using Microsoft.Extensions.Logging;

namespace Granary.App.Services;

/// <summary>
/// Advertises the phone remote with Windows' own DNS-SD registration (<see cref="DnsService"/>), over mDNS: Windows' DNS
/// client answers the queries, and Granary opens no UDP socket. The call is the one the spike made and Windows accepted.
/// The API has no call to change a registration, so a new TXT value is a removal and a new registration. A registration
/// ends with the process at the latest, however it ends.
/// </summary>
/// <remarks>
/// Who owns which memory (Review Focus 3):
/// <list type="bullet">
/// <item>Each registration allocates its own native blocks: the names, the TXT keys and values and their two arrays, the
/// address, and the request (<c>DNS_SERVICE_REGISTER_REQUEST</c>). Windows' <c>DnsServiceConstructInstance</c> copies
/// them into an instance it allocates. All of it, the instance included, stays where it is until Windows confirmed the end
/// of the registration (its refusal, or its removal), because removing takes the same request at the same address; it is
/// freed a moment after that, at a later call, once the callback that confirmed it has long returned. Memory Windows might
/// still read (no answer came, or a removal it did not take) is never freed: the process's end takes it. A registration
/// Windows has not answered for is asked back (<c>DnsServiceDeRegister</c>) before the next one is made, so two of one
/// name do not overlap; which answers are still to come for it is not known then, so its memory is never freed. One whose
/// removal Windows does not take stays the current one, as a registration that went through does, and nothing is made
/// beside it, for a few tries or half a minute (<see cref="DnssdRefusals"/>); then it is given up, its memory kept, and a
/// new one is made. The receiver asks again half a minute after a registration that was not taken
/// (<c>RemoteReceiver.AdvertiseAgainAfter</c>), so that happens without another change and the PC is not left
/// unadvertised while Granary runs.</item>
/// <item>The completion callback is a static <c>UnmanagedCallersOnly</c> function, not a delegate, so nothing of it can
/// be collected while Windows holds its address. Its context is a registration's number, not a handle: a late call finds
/// the number gone and does nothing.</item>
/// <item>The callback runs on a thread of Windows' own. It catches everything, frees an instance Windows handed it when that
/// is not the registration's own (the doc's copy, the callback's to free), and only completes a task.</item>
/// </list>
/// Only status numbers are logged, never a name, an address or a TXT value.
/// </remarks>
public sealed class DnssdServiceAdvertiser(ILogger<DnssdServiceAdvertiser> logger) : IServiceAdvertiser
{
    // How long a registration waits for Windows' word; probing the name on the network takes about a second.
    private static readonly TimeSpan RegisterWait = TimeSpan.FromSeconds(5);

    // How long a removal waits for Windows' word, also before a new registration replaces it.
    private static readonly TimeSpan RemoveWait = TimeSpan.FromSeconds(2);

    // How long after Windows' last word a registration's memory is kept, so its callback has surely returned.
    private static readonly TimeSpan FreeAfter = TimeSpan.FromSeconds(1);

    // How long a registration Windows took neither back nor answered for is watched for a late answer.
    private static readonly TimeSpan LateAnswerWait = TimeSpan.FromSeconds(30);

    // The registrations Windows may still call back for, by the number each request carries as the callback's context.
    private static readonly ConcurrentDictionary<nint, Registration> s_live = new();
    private static long s_numbers;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Registration> _ended = [];
    private Registration? _current;

    public async Task<bool> RegisterAsync(ServiceRegistration registration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration.Address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("The phone remote advertises one IPv4 address.", nameof(registration));
        }
        if (registration.Port is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(registration), "The port is not one a socket can have.");
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FreeEnded();
            if (_current is { } previous)
            {
                _current = null;
                if (!await RemoveAsync(previous).ConfigureAwait(false))
                {
                    // The old one stays, to be taken back at the next try: a new one beside it would advertise the name
                    // twice. Not advertised, the receiver stops and says so.
                    _current = previous;
                    return false;
                }
            }
            return await AddAsync(registration).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnregisterAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            FreeEnded();
            if (_current is { } current)
            {
                _current = null;
                if (!await RemoveAsync(current).ConfigureAwait(false))
                {
                    // Kept, so the next unregistration or registration asks Windows again.
                    _current = current;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // Under the gate. True when Windows confirmed the registration.
    private async Task<bool> AddAsync(ServiceRegistration registration)
    {
        var made = Registration.Make(registration);
        if (made is null)
        {
            logger.LogWarning("Windows could not build the phone remote's DNS-SD record");
            return false;
        }
        uint status = made.Start();
        if (status != DnsService.RequestPending)
        {
            // Windows did not take the request, so it never calls back for it: its memory goes at once.
            made.Free();
            logger.LogWarning("Windows refused the phone remote's advertisement (status {Status})", status);
            return false;
        }
        _current = made;
        uint? answer = await made.AnsweredAsync(RegisterWait).ConfigureAwait(false);
        if (answer is null)
        {
            // Kept: the next registration or removal asks Windows to take it back first.
            logger.LogWarning("Windows did not confirm the phone remote's advertisement in time");
            return false;
        }
        if (answer != DnsService.Success)
        {
            _current = null;
            Ended(made);
            logger.LogWarning("Windows refused the phone remote's advertisement (status {Status})", answer);
            return false;
        }
        logger.LogInformation("The phone remote is advertised on the local network");
        return true;
    }

    // Under the gate. Removes a registration and waits up to RemoveWait for Windows' word. False when Windows did not take
    // the removal of a registration that went through, or of one it has not answered for: it is advertised, or may be yet,
    // and the caller keeps it to ask again, until it is given up (GivenUp).
    private async Task<bool> RemoveAsync(Registration registration)
    {
        if (!registration.IsAnswered)
        {
            return await RemoveUnansweredAsync(registration).ConfigureAwait(false);
        }
        if (!registration.WentThrough)
        {
            // Windows' own word that the registration did not go through: no word on it comes after that. No removal was
            // taken for it (one taken is never asked again), so this is not a removal's word taken for the registration's.
            Ended(registration);
            return true;
        }
        uint status = registration.Remove();
        if (status != DnsService.RequestPending)
        {
            // Windows did not take it, so it never calls back for it: the registration stays, its memory with it.
            logger.LogWarning("Windows did not take the removal of the phone remote's advertisement (status {Status})", status);
            return GivenUp(registration, status);
        }
        if (await registration.RemovedAsync(RemoveWait).ConfigureAwait(false) is not { } removed)
        {
            // Windows holds the removal and may still answer it: not asked again, its memory kept.
            logger.LogWarning("Windows did not confirm the removal of the phone remote's advertisement in time");
            return true;
        }
        if (removed != DnsService.Success)
        {
            logger.LogWarning("Windows removed the phone remote's advertisement with status {Status}", removed);
        }
        Ended(registration);
        return true;
    }

    // Under the gate. Windows has not answered for it yet: it is asked to take it back now, before anything registers
    // anew, so two registrations of one name do not overlap, and given RemoveWait. Which of Windows' answers are still to
    // come is not known then, so its memory is never freed: the process's end takes it. False when Windows did not take
    // the request: it may still go through, so nothing new is made beside it.
    private async Task<bool> RemoveUnansweredAsync(Registration registration)
    {
        uint status = registration.Remove();
        if (status == DnsService.RequestPending)
        {
            // Whose the words from now on are is partly a guess (DnssdAnswers), and the registration's own may never be
            // seen: it is not freed, whatever comes, and is not asked about again.
            if (await registration.RemovedAsync(RemoveWait).ConfigureAwait(false) is null)
            {
                logger.LogWarning("Windows did not confirm the removal of an unanswered advertisement of the phone remote in time");
            }
            return true;
        }
        // Not taken: the caller keeps it as the current one, and once Windows answers, within LateAnswerWait, a registration
        // that went through is removed then; given up meanwhile, it is asked back once all the same.
        logger.LogWarning("Windows did not take back an unanswered advertisement of the phone remote (status {Status})", status);
        if (!registration.IsWatched)
        {
            registration.IsWatched = true;
            _ = RemoveOnceAnsweredAsync(registration);
        }
        return GivenUp(registration, status);
    }

    // Under the gate. Windows refused to take the registration back: true once it is given up (DnssdRefusals), so a new
    // one is made in its place rather than none for as long as Granary runs. Its memory is kept: Windows may still read it.
    private bool GivenUp(Registration registration, uint status)
    {
        if (registration.IsGivenUp)
        {
            return true;
        }
        if (!registration.Refusals.GivesUp(status, Environment.TickCount64))
        {
            return false;
        }
        registration.IsGivenUp = true;
        logger.LogWarning("An advertisement of the phone remote that Windows would not take back is given up; it ends with Granary at the latest");
        return true;
    }

    private async Task RemoveOnceAnsweredAsync(Registration registration)
    {
        try
        {
            if (await registration.AnsweredAsync(LateAnswerWait).ConfigureAwait(false) is null)
            {
                logger.LogWarning("Windows never answered for an advertisement of the phone remote; it ends with Granary");
                return;
            }
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // No longer the current one: a registration or removal since has taken care of it, unless it was given up.
                bool current = ReferenceEquals(_current, registration);
                if (!current && !registration.IsGivenUp)
                {
                    return;
                }
                if (current)
                {
                    _current = null;
                }
                if (!await RemoveAsync(registration).ConfigureAwait(false) && current)
                {
                    // Asked again at the next unregistration or registration.
                    _current = registration;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning("A late advertisement of the phone remote could not be removed ({Error})", RemoteLog.Failure(ex));
        }
    }

    // Under the gate.
    private void Ended(Registration registration)
    {
        registration.EndedAt = Environment.TickCount64;
        _ended.Add(registration);
    }

    // Under the gate: the memory of registrations whose end Windows confirmed a while ago.
    private void FreeEnded()
    {
        long now = Environment.TickCount64;
        foreach (var registration in _ended.Where(ended => now - ended.EndedAt >= (long)FreeAfter.TotalMilliseconds).ToList())
        {
            registration.Free();
            _ended.Remove(registration);
        }
    }

    // DNS_SERVICE_REGISTER_COMPLETE, on Windows' thread: nothing may escape it. The instance Windows hands it is the
    // callback's to free (Microsoft's page for the callback), unless it is the registration's own, which Free frees; the
    // registration's Instance never changes, also once freed, so a call that found it just before Free cannot free it twice.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnComplete(uint status, nint context, nint instance)
    {
        try
        {
            s_live.TryGetValue(context, out var registration);
            if (instance != 0 && (registration is null || instance != registration.Instance))
            {
                DnsService.DnsServiceFreeInstance(instance);
            }
            registration?.Answer(status);
        }
        catch (Exception)
        {
            // An exception on Windows' thread would end the process; at worst one answer is lost, and its wait times out.
        }
    }

    /// <summary>One registration and the native memory it owns.</summary>
    private sealed class Registration
    {
        private readonly List<nint> _blocks = [];
        private readonly DnssdAnswers _answers = new();
        private bool _freed;

        private Registration(nint number) => Number = number;

        /// <summary>The callback's context: the registration is found by it in <see cref="s_live"/>.</summary>
        public nint Number { get; }

        /// <summary>
        /// The DNS_SERVICE_INSTANCE Windows built; the request points to it. Set once as the registration is built and kept
        /// after <see cref="Free"/>, as the address a callback compares with: zeroed there, a callback that found the
        /// registration just before would take the freed instance for one of Windows' copies and free it again.
        /// </summary>
        public nint Instance { get; private set; }

        /// <summary>The DNS_SERVICE_REGISTER_REQUEST, at one address from <see cref="Start"/> until the registration ended.</summary>
        public nint Request { get; private set; }

        public long EndedAt { get; set; }

        /// <summary>Windows' refusals to take it back; under the gate.</summary>
        public DnssdRefusals Refusals { get; } = new();

        /// <summary>Windows would not take it back, and it is no longer kept; under the gate.</summary>
        public bool IsGivenUp { get; set; }

        /// <summary>A late answer is watched for; under the gate.</summary>
        public bool IsWatched { get; set; }

        public bool IsAnswered => _answers.Registered.IsCompleted;

        public bool WentThrough => _answers.Registered.IsCompletedSuccessfully && _answers.Registered.Result == DnsService.Success;

        /// <summary>The registration's blocks and Windows' instance, or null when Windows could not build the instance.</summary>
        public static Registration? Make(ServiceRegistration service)
        {
            var made = new Registration((nint)Interlocked.Increment(ref s_numbers));
            try
            {
                made.Build(service);
            }
            catch
            {
                made.Free();
                throw;
            }
            if (made.Instance == 0)
            {
                made.Free();
                return null;
            }
            return made;
        }

        /// <summary>Hands the request to Windows; <see cref="DnsService.RequestPending"/> when it took it.</summary>
        public uint Start()
        {
            // Findable before Windows has it: the callback can come before DnsServiceRegister returns.
            s_live[Number] = this;
            uint status = DnsService.DnsServiceRegister(Request, 0);
            if (status != DnsService.RequestPending)
            {
                s_live.TryRemove(Number, out _);
            }
            return status;
        }

        /// <summary>Asks Windows to remove the registration made with the same request.</summary>
        public uint Remove()
        {
            _answers.RemovalAsked();
            uint status = DnsService.DnsServiceDeRegister(Request, 0);
            _answers.RemovalAnswered(taken: status == DnsService.RequestPending);
            return status;
        }

        /// <summary>On Windows' thread: its word on the registration, or on its removal.</summary>
        public void Answer(uint status) => _answers.Answer(status);

        /// <summary>Windows' word on the registration, or null when none came within <paramref name="wait"/>.</summary>
        public Task<uint?> AnsweredAsync(TimeSpan wait) => WaitAsync(_answers.Registered, wait);

        /// <summary>Windows' word on the removal, or null when none came within <paramref name="wait"/>.</summary>
        public async Task<uint?> RemovedAsync(TimeSpan wait)
        {
            if (await WaitAsync(_answers.Removed, wait).ConfigureAwait(false) is { } removed)
            {
                return removed;
            }
            // For a registration not answered before: a word alone, undecided until now, counts as the removal's.
            _answers.RemovalWaitEnded();
            return _answers.Removed.IsCompletedSuccessfully ? _answers.Removed.Result : null;
        }

        /// <summary>
        /// Frees Windows' instance and every block, once: only when Windows will not call back for it any more, or never
        /// took it. It is first made unfindable, so a call that still came would touch none of it.
        /// </summary>
        public void Free()
        {
            if (_freed)
            {
                return;
            }
            _freed = true;
            s_live.TryRemove(Number, out _);
            if (Instance != 0)
            {
                DnsService.DnsServiceFreeInstance(Instance);
            }
            foreach (nint block in _blocks)
            {
                Marshal.FreeHGlobal(block);
            }
            _blocks.Clear();
            Request = 0;
        }

        private static async Task<uint?> WaitAsync(Task<uint> answer, TimeSpan wait)
        {
            try
            {
                return await answer.WaitAsync(wait).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        // The instance from copies of the registration's values, and the request that points to it.
        private unsafe void Build(ServiceRegistration service)
        {
            nint name = Text($"{service.InstanceName}.{service.ServiceType}.local");
            nint host = Text(service.HostName);
            // IP4_ADDRESS: the four bytes in network order, as the address writes them.
            nint ip4 = Block(sizeof(uint));
            if (!service.Address.TryWriteBytes(new Span<byte>((void*)ip4, sizeof(uint)), out int written) || written != sizeof(uint))
            {
                throw new ArgumentException("The phone remote advertises one IPv4 address.", nameof(service));
            }
            int count = service.Txt.Count;
            nint keys = Block(Math.Max(1, count) * sizeof(nint));
            nint values = Block(Math.Max(1, count) * sizeof(nint));
            for (int i = 0; i < count; i++)
            {
                ((nint*)keys)[i] = Text(service.Txt[i].Key);
                ((nint*)values)[i] = Text(service.Txt[i].Value);
            }
            Instance = DnsService.DnsServiceConstructInstance(name, host, ip4, 0, (ushort)service.Port, 0, 0, (uint)count, keys, values);
            if (Instance == 0)
            {
                return;
            }
            Request = Block(sizeof(DnsService.RegisterRequest));
            *(DnsService.RegisterRequest*)Request = new DnsService.RegisterRequest
            {
                Version = DnsService.RequestVersion1,
                InterfaceIndex = service.InterfaceIndex,
                ServiceInstance = Instance,
                RegisterCompletionCallback = (nint)(delegate* unmanaged[Stdcall]<uint, nint, nint, void>)&OnComplete,
                QueryContext = Number,
                Credentials = 0,
                UnicastEnabled = 0,
            };
        }

        private unsafe nint Block(int bytes)
        {
            nint block = Marshal.AllocHGlobal(bytes);
            _blocks.Add(block);
            new Span<byte>((void*)block, bytes).Clear();
            return block;
        }

        private nint Text(string text)
        {
            nint block = Marshal.StringToHGlobalUni(text);
            _blocks.Add(block);
            return block;
        }
    }
}
