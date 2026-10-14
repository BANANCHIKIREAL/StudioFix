using System;
using System.IO;
using System.Threading;
public static class FixtureStudio
{
    public static void Main()
    {
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixture-started.txt"), Environment.CurrentDirectory);
        Thread.Sleep(150);
    }
}
