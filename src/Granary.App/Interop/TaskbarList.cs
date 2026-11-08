using System.Runtime.InteropServices;

namespace Granary.App.Interop;

/// <summary>A button of a window's thumbnail toolbar, the row of buttons under its taskbar preview.</summary>
internal readonly record struct ThumbButton(uint Id, nint Icon, string Tip, bool IsHidden, bool IsEnabled);

/// <summary>
/// Explorer's ITaskbarList3 (CLSID_TaskbarList), its thumbnail toolbar only, via the COM vtable. Made and used on the UI
/// thread; released with <see cref="Dispose"/>.
/// </summary>
internal sealed unsafe partial class TaskbarList : IDisposable
{
    private static readonly Guid ClassId = new("56fdf344-fd6d-11d0-958a-006097c9a090");
    private static readonly Guid InterfaceId = new("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf");

    private const uint InProcessServer = 1;

    // vtable: IUnknown (3); ITaskbarList: HrInit … SetActiveAlt (5); ITaskbarList2: MarkFullscreenWindow; ITaskbarList3:
    // SetProgressValue … SetTabActive (6), then ThumbBarAddButtons and ThumbBarUpdateButtons.
    private const int HrInitSlot = 3;
    private const int ThumbBarAddButtonsSlot = 15;
    private const int ThumbBarUpdateButtonsSlot = 16;

    // THUMBBUTTONMASK: THB_ICON | THB_TOOLTIP | THB_FLAGS. THUMBBUTTONFLAGS: THBF_DISABLED, THBF_HIDDEN.
    private const uint MaskIconTipFlags = 0x2 | 0x4 | 0x8;
    private const uint FlagDisabled = 0x1;
    private const uint FlagHidden = 0x8;
    private const int TipLength = 260;

    private nint _list;

    private TaskbarList(nint list) => _list = list;

    public static TaskbarList Create()
    {
        Guid classId = ClassId;
        Guid interfaceId = InterfaceId;
        Marshal.ThrowExceptionForHR(CoCreateInstance(&classId, 0, InProcessServer, &interfaceId, out nint list));
        var taskbar = new TaskbarList(list);
        try
        {
            var hrInit = (delegate* unmanaged[Stdcall]<nint, int>)(*(nint**)list)[HrInitSlot];
            Marshal.ThrowExceptionForHR(hrInit(list));
            return taskbar;
        }
        catch
        {
            taskbar.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Gives the window's toolbar its buttons, in their order there; a toolbar takes its buttons once per taskbar button,
    /// all in one call.
    /// </summary>
    public void AddButtons(nint hwnd, ReadOnlySpan<ThumbButton> buttons) => Call(ThumbBarAddButtonsSlot, hwnd, buttons);

    /// <summary>Changes buttons added before, each found by its id.</summary>
    public void UpdateButtons(nint hwnd, ReadOnlySpan<ThumbButton> buttons) => Call(ThumbBarUpdateButtonsSlot, hwnd, buttons);

    public void Dispose()
    {
        if (_list != 0)
        {
            Marshal.Release(_list);
            _list = 0;
        }
    }

    private void Call(int slot, nint hwnd, ReadOnlySpan<ThumbButton> buttons)
    {
        ObjectDisposedException.ThrowIf(_list == 0, this);
        var natives = new NativeThumbButton[buttons.Length];
        for (int i = 0; i < buttons.Length; i++)
        {
            var button = buttons[i];
            var native = new NativeThumbButton
            {
                Mask = MaskIconTipFlags,
                Id = button.Id,
                Icon = button.Icon,
                Flags = (button.IsHidden ? FlagHidden : 0) | (button.IsEnabled ? 0 : FlagDisabled),
            };
            // Zero-filled: the tip ends with a null.
            button.Tip.AsSpan(0, Math.Min(button.Tip.Length, TipLength - 1)).CopyTo(new Span<char>(native.Tip, TipLength));
            natives[i] = native;
        }
        var method = (delegate* unmanaged[Stdcall]<nint, nint, uint, NativeThumbButton*, int>)(*(nint**)_list)[slot];
        fixed (NativeThumbButton* first = natives)
        {
            Marshal.ThrowExceptionForHR(method(_list, hwnd, (uint)natives.Length, first));
        }
    }

    // THUMBBUTTON (shobjidl_core.h).
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeThumbButton
    {
        public uint Mask;
        public uint Id;
        public uint Bitmap;
        public nint Icon;
        public fixed char Tip[TipLength];
        public uint Flags;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* classId, nint outer, uint context, Guid* interfaceId, out nint instance);
}
