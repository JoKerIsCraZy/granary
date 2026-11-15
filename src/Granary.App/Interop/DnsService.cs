using System.Runtime.InteropServices;

namespace Granary.App.Interop;

/// <summary>
/// Windows' DNS-SD registration (<c>windns.h</c>, <c>dnsapi.dll</c>, Windows 10 and later, desktop apps), as Microsoft's
/// pages for <c>DnsServiceConstructInstance</c>, <c>DnsServiceRegister</c>, <c>DnsServiceDeRegister</c>,
/// <c>DNS_SERVICE_REGISTER_REQUEST</c> and <c>DNS_SERVICE_REGISTER_COMPLETE</c> give it. Every parameter is a plain number
/// or pointer, so no marshalling stub runs: who owns which memory is the caller's to say
/// (<c>Services/DnssdServiceAdvertiser</c>).
/// </summary>
internal static partial class DnsService
{
    /// <summary><c>DNS_QUERY_REQUEST_VERSION1</c>, the only version of the request.</summary>
    public const uint RequestVersion1 = 1;

    /// <summary><c>DNS_REQUEST_PENDING</c> (9506): Windows took the request and calls back when it is done.</summary>
    public const uint RequestPending = 9506;

    /// <summary><c>ERROR_SUCCESS</c>, the status of a registration or removal that went through.</summary>
    public const uint Success = 0;

    /// <summary>
    /// Builds a newly allocated <c>DNS_SERVICE_INSTANCE</c> from copies of the given values; the caller frees it with
    /// <see cref="DnsServiceFreeInstance"/>. Null when it could not.
    /// </summary>
    /// <param name="serviceName">PCWSTR: <c>instance._service._tcp.local</c>.</param>
    /// <param name="hostName">PCWSTR: the host whose address record names <paramref name="ip4"/>.</param>
    /// <param name="ip4">PIP4_ADDRESS: four bytes in network order.</param>
    /// <param name="ip6">PIP6_ADDRESS: none here, 0.</param>
    /// <param name="port">The port, in host order.</param>
    /// <param name="priority">The SRV priority.</param>
    /// <param name="weight">The SRV weight.</param>
    /// <param name="propertiesCount">How many TXT pairs <paramref name="keys"/> and <paramref name="values"/> hold.</param>
    /// <param name="keys">PCWSTR*: the TXT keys.</param>
    /// <param name="values">PCWSTR*: the TXT values, in the keys' order.</param>
    [LibraryImport("dnsapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint DnsServiceConstructInstance(nint serviceName, nint hostName, nint ip4, nint ip6, ushort port, ushort priority,
        ushort weight, uint propertiesCount, nint keys, nint values);

    /// <summary>Frees an instance <see cref="DnsServiceConstructInstance"/> made, or one Windows handed to the completion callback.</summary>
    [LibraryImport("dnsapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial void DnsServiceFreeInstance(nint instance);

    /// <summary>
    /// Starts the registration <paramref name="request"/> (a <see cref="RegisterRequest"/>) describes; <see cref="RequestPending"/>
    /// when Windows took it, and then the request's callback says how it went. The request must stay at its address until
    /// the registration has ended: removing it takes the same request. The registration ends with the process at the latest.
    /// </summary>
    /// <param name="request">PDNS_SERVICE_REGISTER_REQUEST.</param>
    /// <param name="cancel">PDNS_SERVICE_CANCEL: none here, 0.</param>
    [LibraryImport("dnsapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint DnsServiceRegister(nint request, nint cancel);

    /// <summary>
    /// Starts removing the registration made with <paramref name="request"/>, the same request at the same address;
    /// <see cref="RequestPending"/> when Windows took it, and then the request's callback says it is done.
    /// </summary>
    /// <param name="request">The PDNS_SERVICE_REGISTER_REQUEST the registration was made with.</param>
    /// <param name="cancel">Must be 0.</param>
    [LibraryImport("dnsapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint DnsServiceDeRegister(nint request, nint cancel);

    /// <summary>
    /// <c>DNS_SERVICE_REGISTER_REQUEST</c>, 48 bytes on x64. The callback is a
    /// <c>VOID WINAPI DNS_SERVICE_REGISTER_COMPLETE(DWORD Status, PVOID pQueryContext, PDNS_SERVICE_INSTANCE pInstance)</c>;
    /// it is called on a thread of Windows' own, for the registration and again for its removal.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RegisterRequest
    {
        /// <summary>ULONG: <see cref="RequestVersion1"/>.</summary>
        public uint Version;

        /// <summary>ULONG: the adapter to advertise on; 0 would be every adapter.</summary>
        public uint InterfaceIndex;

        /// <summary>PDNS_SERVICE_INSTANCE: the instance to register.</summary>
        public nint ServiceInstance;

        /// <summary>PDNS_SERVICE_REGISTER_COMPLETE: the completion callback.</summary>
        public nint RegisterCompletionCallback;

        /// <summary>PVOID: handed back to the callback as is.</summary>
        public nint QueryContext;

        /// <summary>HANDLE: not used, 0.</summary>
        public nint Credentials;

        /// <summary>BOOL: FALSE for mDNS on the local link; TRUE would be unicast DNS.</summary>
        public int UnicastEnabled;
    }
}
