using Granary.Core.Downloads;

namespace Granary.Tests.Fakes;

/// <summary>A drive whose free space cannot be read at all; its <see cref="Message"/> must stay out of the log.</summary>
internal sealed class FailingDiskSpace : IDiskSpace
{
    public const string Message = @"The drive at D:\Elena failed.";

    public long? FreeBytes(string folder) => throw new IOException(Message);
}
