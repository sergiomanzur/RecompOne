using System;

namespace RecompOne.Runtime;

public class StateLoadedException : Exception
{
    public StateLoadedException() : base("Savestate loaded") { }
}
