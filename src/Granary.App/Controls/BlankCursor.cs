using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using WinRT;
using WinRT.Interop;

namespace Granary.App.Controls;

/// <summary>
/// A cursor that shows nothing, for <c>ProtectedCursor</c>: over XAML content the cursor is the element's, set by
/// WinUI's input stack, so <c>ShowCursor</c> has no effect there, and InputSystemCursor has no hidden shape. This is a
/// fully transparent Win32 cursor made at run time, wrapped through InputCursor's documented interop
/// (<c>IInputCursorStaticsInterop.CreateFromHCursor</c>). It lives as long as the app.
/// </summary>
internal static partial class BlankCursor
{
    private static readonly Guid StaticsInteropId = new("ac6f5065-90c4-46ce-beb7-05e138e54117");

    // SM_CXCURSOR and SM_CYCURSOR: the size the system draws cursors at.
    private const int CursorWidthMetric = 13;
    private const int CursorHeightMetric = 14;

    private static readonly Lazy<InputCursor?> Made = new(Create);

    /// <summary>The blank cursor, or null where it could not be made (the element then keeps its own).</summary>
    public static InputCursor? Instance => Made.Value;

    private static InputCursor? Create()
    {
        try
        {
            return Make();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or EntryPointNotFoundException)
        {
            App.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(BlankCursor))
                .LogWarning(ex, "Could not make a blank cursor; the cursor stays over the picture");
            return null;
        }
    }

    private static unsafe InputCursor? Make()
    {
        int width = Math.Max(1, GetSystemMetrics(CursorWidthMetric));
        int height = Math.Max(1, GetSystemMetrics(CursorHeightMetric));
        // Rows are padded to whole 16-bit words. AND all ones keeps the screen, XOR all zeros changes nothing.
        int planeSize = (width + 15) / 16 * 2 * height;
        byte[] andPlane = new byte[planeSize];
        Array.Fill(andPlane, (byte)0xFF);
        byte[] xorPlane = new byte[planeSize];
        nint cursor = CreateCursor(GetModuleHandleW(null), 0, 0, width, height, andPlane, xorPlane);
        if (cursor == 0)
        {
            throw new COMException("CreateCursor failed", Marshal.GetHRForLastWin32Error());
        }
        using var factory = ActivationFactory.Get("Microsoft.UI.Input.InputCursor");
        using var interop = factory.As<IUnknownVftbl>(StaticsInteropId);
        nint self = interop.ThisPtr;
        nint result = 0;
        // IInspectable's six slots come first; CreateFromHCursor is the seventh.
        var createFromHCursor = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)(*(void***)self)[6];
        Marshal.ThrowExceptionForHR(createFromHCursor(self, cursor, &result));
        try
        {
            return MarshalInspectable<InputCursor>.FromAbi(result);
        }
        finally
        {
            Marshal.Release(result);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreateCursor(nint instance, int hotSpotX, int hotSpotY, int width, int height, byte[] andPlane, byte[] xorPlane);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? moduleName);
}
