using System.Collections.Generic;
using System.Reflection;
using Nini.Config;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.SuggestionService
{
    // Backing service for the WebInterface Suggestion Box - see
    // ISuggestionService/ISuggestionData for the design rationale.
    public class SuggestionService : SuggestionServiceBase, ISuggestionService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        public SuggestionService(IConfigSource config)
            : base(config)
        {
            m_log.Debug("[SUGGESTION SERVICE]: Starting suggestion service");
        }

        public Suggestion Get(UUID id)
        {
            return m_Database.Get(id);
        }

        public List<Suggestion> GetByUser(UUID userId, int start, int count)
        {
            return m_Database.GetByUser(userId, start, count);
        }

        public List<Suggestion> GetAll(int start, int count)
        {
            return m_Database.GetAll(start, count);
        }

        public bool Store(Suggestion suggestion)
        {
            return m_Database.Store(suggestion);
        }
    }
}
