using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Services.Interfaces
{
    public interface ISuggestionService
    {
        Suggestion Get(UUID id);
        List<Suggestion> GetByUser(UUID userId, int start, int count);
        List<Suggestion> GetAll(int start, int count);
        bool Store(Suggestion suggestion);
    }
}
