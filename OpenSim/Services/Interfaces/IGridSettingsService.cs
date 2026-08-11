using System.Collections.Generic;

namespace OpenSim.Services.Interfaces
{
    // Backing service for the WebInterface grid settings editor - see
    // OpenSim.Data.IGridSettingsData for the design rationale.
    public interface IGridSettingsService
    {
        string Get(string key);
        Dictionary<string, string> GetAll();
        bool Set(string key, string value);
    }
}
