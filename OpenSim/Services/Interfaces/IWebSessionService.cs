using OpenSim.Framework;

namespace OpenSim.Services.Interfaces
{
    // Service-level wrapper around IWebSessionData, same shape as
    // IGridSettingsService - lets WebInterfaceServiceConnector.cs load it
    // through the same LoadReusedPlugin("...", "LocalServiceModule", ...)
    // convention every other service in that file already uses.
    public interface IWebSessionService
    {
        WebSessionRecord Get(string token);
        bool Store(WebSessionRecord session);
        bool Delete(string token);
    }
}
