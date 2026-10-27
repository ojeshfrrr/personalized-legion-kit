using System;
using LenovoLegionToolkit.Lib.Utils;

// No App, IoC, hardware services, elevation request or production profile initialization.
try
{
    if (args.Length != 2) return 2;
    await PortableUpdateInstaller.RunWorkerAsync(args[0], args[1]);
    return 0;
}
catch { return 1; }
