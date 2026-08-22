using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data
{
    // Backing store for the WebInterface Suggestion Box - see
    // OpenSim.Framework.Suggestion for the design rationale.
    public interface ISuggestionData
    {
        Suggestion Get(UUID id);

        // Most recent first, scoped to one resident's own suggestions.
        List<Suggestion> GetByUser(UUID userId, int start, int count);

        // Most recent first, every suggestion - the admin queue.
        List<Suggestion> GetAll(int start, int count);

        bool Store(Suggestion suggestion);
    }
}
