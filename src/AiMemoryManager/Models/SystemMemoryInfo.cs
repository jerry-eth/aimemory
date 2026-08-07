namespace AiMemoryManager.Models;

public record SystemMemoryInfo(long TotalBytes, long AvailableBytes)
{
    public double UsedPercent => TotalBytes == 0 ? 0 : (1.0 - (double)AvailableBytes / TotalBytes) * 100;
}
