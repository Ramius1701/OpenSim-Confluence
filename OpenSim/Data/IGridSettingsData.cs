using System.Collections.Generic;

namespace OpenSim.Data
{
    // Backing store for the WebInterface grid settings editor (see
    // PROJECT_LOG.md Batch 14) - a small admin-editable key/value override
    // layer on top of a handful of values that would otherwise only ever
    // come from Robust's own .ini and need a restart to change. Deliberately
    // generic key/value rather than one column per setting, since the set
    // of editable values is expected to grow (see WebInterfaceServiceConnector's
    // GetSetting helper for the actual defined keys) and a new column +
    // migration across three DB backends per new setting would be a lot of
    // ceremony for what's fundamentally just named strings.
    public interface IGridSettingsData
    {
        string Get(string key);
        Dictionary<string, string> GetAll();
        bool Set(string key, string value);
    }
}
