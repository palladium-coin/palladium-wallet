using System.Runtime.CompilerServices;

namespace PalladiumWallet.Tests;

internal static class ThreadPoolSetup
{
    // Property tests saturate the pool while network tests wait on continuations; with the
    // default minimum the pool injects threads slowly and a loopback handshake can stall for
    // seconds. A higher floor removes that starvation so the request timeouts stay meaningful.
    [ModuleInitializer]
    internal static void Init() => ThreadPool.SetMinThreads(256, 256);
}
