namespace DalamudMCP.Plugin.Tools;

internal sealed class RetainerWindowOwner
{
    private ulong character;
    private ulong activeRetainer;
    private bool sawClosed;
    private bool ownerChanged;
    private long visibleSince;

    internal ulong Observe(ulong characterID, bool open, ulong selected, long now)
    {
        if (character != characterID)
        {
            character = characterID;
            activeRetainer = 0;
            sawClosed = false;
            ownerChanged = false;
        }
        if (characterID == 0 || !open)
        {
            activeRetainer = 0;
            ownerChanged = false;
            sawClosed = characterID != 0;
            return 0;
        }
        if (!sawClosed || ownerChanged || selected == 0) return 0;
        if (activeRetainer == 0)
        {
            activeRetainer = selected;
            visibleSince = now;
        }
        else if (activeRetainer != selected)
        {
            ownerChanged = true;
            return 0;
        }
        return now - visibleSince >= 250 ? selected : 0;
    }

}
