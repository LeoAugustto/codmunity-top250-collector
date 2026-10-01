namespace CODMunityTop250Collector.Core;

public sealed class SingleInstanceLifetime
{
    private bool ownsMutex;
    private bool released;

    public bool OwnsMutex => ownsMutex && !released;
    public void SetOwnership(bool owns) => ownsMutex = owns;

    public bool ReleaseIfOwned(Action release)
    {
        if (!OwnsMutex) return false;
        released = true;
        release();
        return true;
    }
}
