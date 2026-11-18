using Skua.Core.Interfaces;
using System;

namespace Skua.App.Avalonia.Services;

public class SoundService : ISoundService
{
    public void Beep()
    {
        try { Console.Beep(); } catch { }
    }

    public void Beep(int frequency, int duration)
    {
        if (frequency < 37)
            frequency = 37;
        else if (frequency > 32767)
            frequency = 32767;

        if (OperatingSystem.IsWindows())
            Console.Beep(frequency, duration);
        else
            try { Console.Beep(); } catch { }
    }
}
