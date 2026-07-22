using McPanel.Api.Configuration;

namespace McPanel.Api.Infrastructure;

public static class MemorySizing
{
    public static int TotalForExistingHeapMb(int maximumHeapMb)
    {
        const int maximumReserveMb = 4 * 1024;
        var reserve = Math.Clamp(
            RoundUpToStep((int)Math.Ceiling(maximumHeapMb / 4d)),
            PanelOptions.ServerMemoryStepMb,
            maximumReserveMb);
        return checked(maximumHeapMb + reserve);
    }

    private static int RoundUpToStep(int value) =>
        checked((value + PanelOptions.ServerMemoryStepMb - 1) / PanelOptions.ServerMemoryStepMb * PanelOptions.ServerMemoryStepMb);
}
