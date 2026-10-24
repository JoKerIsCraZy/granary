<#
.SYNOPSIS
    Captures OutputDebugString messages (the DBWIN protocol) for a number of seconds, without a debugger.
.DESCRIPTION
    WinUI writes the details of XAML parse errors there (the failing resource key or element), which the
    XamlParseException itself does not carry. Start it before reproducing; -ProcessId 0 takes every process.
    It does not work while a debugger is attached to the app, since the debugger receives the messages.
.EXAMPLE
    ./scripts/capture-debug-output.ps1 -ProcessId 0 -Seconds 40 -OutFile debug.txt
#>
param([int]$ProcessId, [int]$Seconds = 30, [Parameter(Mandatory)] [string]$OutFile)

Add-Type @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class DbWin
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string name);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileMapping(IntPtr file, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern bool SetEvent(IntPtr handle);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    public static void Capture(int pid, int seconds, string outFile)
    {
        IntPtr bufferReady = CreateEvent(IntPtr.Zero, false, false, "DBWIN_BUFFER_READY");
        IntPtr dataReady = CreateEvent(IntPtr.Zero, false, false, "DBWIN_DATA_READY");
        IntPtr mapping = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, 4, 0, 4096, "DBWIN_BUFFER");
        IntPtr view = MapViewOfFile(mapping, 4, 0, 0, new UIntPtr(4096));
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        using (var writer = new StreamWriter(outFile, false, Encoding.UTF8))
        {
            SetEvent(bufferReady);
            while (DateTime.UtcNow < deadline)
            {
                if (WaitForSingleObject(dataReady, 500) == 0)
                {
                    int sender = Marshal.ReadInt32(view);
                    string text = Marshal.PtrToStringAnsi(IntPtr.Add(view, 4)) ?? "";
                    if (pid == 0 || sender == pid)
                    {
                        writer.WriteLine(text.TrimEnd());
                        writer.Flush();
                    }
                    SetEvent(bufferReady);
                }
            }
        }
    }
}
'@
[DbWin]::Capture($ProcessId, $Seconds, $OutFile)
